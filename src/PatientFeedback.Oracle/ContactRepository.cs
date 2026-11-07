using System.Data;
using Dapper;
using PatientFeedback.Dispatch;
using PatientFeedback.Privacy;

namespace PatientFeedback.Oracle;

public sealed record RevealedContact(string ContactId, string FullName, string Phone);

/// <summary>Contact-level operations: opt-out and audited reveal of the decrypted contact details.</summary>
public sealed class ContactRepository(OracleDb db, PiiProtector protector, BlindIndex blindIndex)
{
    /// <summary>
    /// Inbound "STOP"/"IPTAL" reply from a provider. The phone number is looked up through its blind index, so no
    /// plaintext phone column is needed; every contact sharing that number (e.g. a parent's phone) is opted out.
    /// </summary>
    public async Task<int> OptOutByPhoneAsync(string phone, Channel channel, string actor, DateTimeOffset now, CancellationToken ct = default)
    {
        var phoneBidx = blindIndex.Compute(phone);
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var contactIds = (await connection.QueryAsync<string>(
            "SELECT contact_id FROM contact WHERE phone_bidx = :phoneBidx AND anonymised_at IS NULL FOR UPDATE",
            new { phoneBidx }, tx)).ToList();
        foreach (var contactId in contactIds)
        {
            await OptOutAsync(connection, tx, contactId, [channel], actor, "inbound-stop-reply", now);
        }

        await tx.CommitAsync(ct);
        return contactIds.Count;
    }

    internal static async Task OptOutAsync(IDbConnection connection, IDbTransaction tx, string contactId, IReadOnlyCollection<Channel> channels, string actor, string purpose, DateTimeOffset now)
    {
        if (channels.Contains(Channel.WhatsApp))
        {
            await connection.ExecuteAsync(
                "UPDATE contact SET whatsapp_opt_out_at = :now WHERE contact_id = :contactId AND whatsapp_opt_out_at IS NULL",
                new { now, contactId }, tx);
        }

        if (channels.Contains(Channel.Sms))
        {
            await connection.ExecuteAsync(
                "UPDATE contact SET sms_opt_out_at = :now WHERE contact_id = :contactId AND sms_opt_out_at IS NULL",
                new { now, contactId }, tx);
        }

        // Pending messages for this contact on opted-out channels are not sent; the dispatcher re-routes them.
        await AuditRepository.WriteAsync(connection, tx,
            new AuditEntry(actor, AuditActions.OptOut, "contact", contactId, string.Join(',', channels.Select(c => c.ToDb())), purpose));
    }

    /// <summary>
    /// Decrypts name and phone for a case handler who must call the patient back. The audit row is written in the
    /// same transaction before the plaintext leaves this method, with the purpose the handler selected.
    /// </summary>
    public async Task<RevealedContact?> RevealAsync(string contactId, string actor, string purpose, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ContactSecretRow>(
            """
            SELECT full_name_ct AS FullNameCt, phone_ct AS PhoneCt, wrapped_dek AS WrappedDek, kek_id AS KekId
              FROM contact
             WHERE contact_id = :contactId AND anonymised_at IS NULL
            """, new { contactId }, tx);
        if (row is null)
        {
            return null;
        }

        await AuditRepository.WriteAsync(connection, tx,
            new AuditEntry(actor, AuditActions.PiiReveal, "contact", contactId, "full_name,phone", purpose));
        await tx.CommitAsync(ct);

        using var key = protector.Unwrap(new WrappedKey(row.KekId, row.WrappedDek), contactId);
        return new RevealedContact(
            contactId,
            PiiProtector.Decrypt(key, contactId, ContactFields.FullName, row.FullNameCt),
            PiiProtector.Decrypt(key, contactId, ContactFields.Phone, row.PhoneCt));
    }

    /// <summary>Re-wraps every live data key under the active KEK (KEK rotation). Field ciphertexts are untouched.</summary>
    public async Task<int> RewrapKeysAsync(int batchSize, CancellationToken ct = default)
    {
        var total = 0;
        while (true)
        {
            await using var connection = await db.OpenAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            var rows = (await connection.QueryAsync<RewrapRow>(
                """
                SELECT contact_id AS ContactId, wrapped_dek AS WrappedDek, kek_id AS KekId
                  FROM contact
                 WHERE anonymised_at IS NULL AND kek_id <> :active
                 FETCH FIRST :batchSize ROWS ONLY
                """, new { active = protector.ActiveKekId, batchSize }, tx)).ToList();
            if (rows.Count == 0)
            {
                return total;
            }

            foreach (var r in rows)
            {
                var rewrapped = protector.Rewrap(new WrappedKey(r.KekId, r.WrappedDek), r.ContactId);
                // Optimistic: FOR UPDATE cannot be combined with FETCH FIRST (ORA-02014); a row changed meanwhile is skipped
                // and picked up by the next batch.
                await connection.ExecuteAsync(
                    "UPDATE contact SET wrapped_dek = :Blob, kek_id = :KekId WHERE contact_id = :ContactId AND kek_id = :OldKekId AND anonymised_at IS NULL",
                    new { rewrapped.Blob, rewrapped.KekId, r.ContactId, OldKekId = r.KekId }, tx);
            }

            await tx.CommitAsync(ct);
            total += rows.Count;
        }
    }

    private sealed class ContactSecretRow
    {
        public byte[] FullNameCt { get; init; } = [];

        public byte[] PhoneCt { get; init; } = [];

        public byte[] WrappedDek { get; init; } = [];

        public string KekId { get; init; } = "";
    }

    private sealed class RewrapRow
    {
        public string ContactId { get; init; } = "";

        public byte[] WrappedDek { get; init; } = [];

        public string KekId { get; init; } = "";
    }
}
