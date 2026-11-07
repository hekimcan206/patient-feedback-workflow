using System.Data;
using Dapper;
using PatientFeedback.Dispatch;

namespace PatientFeedback.Oracle;

/// <summary>A claimed outbox row joined with what the dispatcher needs to send it.</summary>
public sealed class ClaimedMessage
{
    public long MessageId { get; init; }

    public long SurveyId { get; init; }

    public string ChannelCode { get; init; } = "";

    public int Attempts { get; init; }

    public int TotalAttempts { get; init; }

    public byte[]? TokenCt { get; init; }

    public string ContactId { get; init; } = "";

    public byte[]? PhoneCt { get; init; }

    public byte[]? WrappedDek { get; init; }

    public string? KekId { get; init; }

    public string PreferredChannelCode { get; init; } = "";

    public DateTimeOffset? WhatsAppOptOutAt { get; init; }

    public DateTimeOffset? SmsOptOutAt { get; init; }

    public string Department { get; init; } = "";

    public DateTimeOffset ExpiresAt { get; init; }

    public Channel Channel => Db.ToChannel(ChannelCode);

    public Channel PreferredChannel => Db.ToChannel(PreferredChannelCode);

    public IReadOnlySet<Channel> OptedOut
    {
        get
        {
            var set = new HashSet<Channel>();
            if (WhatsAppOptOutAt is not null)
            {
                set.Add(Channel.WhatsApp);
            }

            if (SmsOptOutAt is not null)
            {
                set.Add(Channel.Sms);
            }

            return set;
        }
    }
}

/// <summary>
/// Outbox persistence. Every state change after a claim is fenced by the claim token: if this worker's lease expired
/// and another worker re-claimed the row, the late write matches zero rows instead of overwriting the newer state.
/// </summary>
public sealed class OutboxRepository(OracleDb db)
{
    public async Task<(Guid ClaimToken, IReadOnlyList<ClaimedMessage> Messages)> ClaimAsync(int limit, TimeSpan lease, DateTimeOffset now, CancellationToken ct = default)
    {
        var claimToken = Guid.NewGuid();
        await using var connection = await db.OpenAsync(ct);
        var p = new DynamicParameters(new { limit, lease = (int)Math.Ceiling(lease.TotalSeconds), now, token = claimToken.ToByteArray() });
        p.Add("claimed", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition("BEGIN outbox_pkg.claim(:limit, :lease, :now, :token, :claimed); END;", p, cancellationToken: ct));
        if (p.ReadId("claimed") == 0)
        {
            return (claimToken, []);
        }

        var rows = await connection.QueryAsync<ClaimedMessage>(new CommandDefinition(
            """
            SELECT o.message_id AS MessageId, o.survey_id AS SurveyId, o.channel AS ChannelCode, o.attempts AS Attempts,
                   o.total_attempts AS TotalAttempts, o.token_ct AS TokenCt,
                   c.contact_id AS ContactId, c.phone_ct AS PhoneCt, c.wrapped_dek AS WrappedDek, c.kek_id AS KekId,
                   c.preferred_channel AS PreferredChannelCode, c.whatsapp_opt_out_at AS WhatsAppOptOutAt,
                   c.sms_opt_out_at AS SmsOptOutAt, s.department AS Department, s.expires_at AS ExpiresAt
              FROM outbox_message o
              JOIN survey s ON s.survey_id = o.survey_id
              JOIN contact c ON c.contact_id = s.contact_id
             WHERE o.claim_token = :token
            """, new { token = claimToken.ToByteArray() }, cancellationToken: ct));
        return (claimToken, [.. rows]);
    }

    /// <summary>One PII_READ row per contact whose phone the dispatcher is about to decrypt.</summary>
    public async Task AuditDispatchReadsAsync(IReadOnlyCollection<string> contactIds, string actor, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await AuditRepository.WriteAsync(connection, null,
            [.. contactIds.Select(id => new AuditEntry(actor, AuditActions.PiiRead, "contact", id, "phone", "survey-dispatch"))]);
    }

    public async Task MarkSentAsync(ClaimedMessage m, Guid claimToken, string providerMessageId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var updated = await connection.ExecuteAsync(
            """
            UPDATE outbox_message
               SET status = 'SENT', sent_at = :now, provider_message_id = :providerMessageId, token_ct = NULL,
                   attempts = attempts + 1, total_attempts = total_attempts + 1, lease_until = NULL, last_error = NULL
             WHERE message_id = :MessageId AND claim_token = :token AND status = 'IN_FLIGHT'
            """, new { now, providerMessageId, m.MessageId, token = claimToken.ToByteArray() }, tx);
        if (updated == 1)
        {
            await connection.ExecuteAsync(
                "UPDATE survey SET status = 'SENT', sent_at = :now WHERE survey_id = :SurveyId AND status = 'SCHEDULED'",
                new { now, m.SurveyId }, tx);
        }

        await tx.CommitAsync(ct);
    }

    public async Task RetryAsync(ClaimedMessage m, Guid claimToken, DateTimeOffset nextAttemptAt, string? error, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE outbox_message
               SET status = 'PENDING', attempts = attempts + 1, total_attempts = total_attempts + 1,
                   next_attempt_at = :nextAttemptAt, last_error = :error, lease_until = NULL, claim_token = NULL
             WHERE message_id = :MessageId AND claim_token = :token AND status = 'IN_FLIGHT'
            """, new { nextAttemptAt, error, m.MessageId, token = claimToken.ToByteArray() }, cancellationToken: ct));
    }

    public async Task FallBackAsync(ClaimedMessage m, Guid claimToken, Channel next, string? error, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE outbox_message
               SET status = 'PENDING', channel = :channel, attempts = 0, total_attempts = total_attempts + 1,
                   next_attempt_at = :now, last_error = :error, lease_until = NULL, claim_token = NULL
             WHERE message_id = :MessageId AND claim_token = :token AND status = 'IN_FLIGHT'
            """, new { channel = next.ToDb(), now, error, m.MessageId, token = claimToken.ToByteArray() }, cancellationToken: ct));
    }

    /// <summary>Ends delivery: DEAD (all channels failed) or CANCELLED (opted out, link expired, contact anonymised).</summary>
    public async Task FinishAsync(ClaimedMessage m, Guid claimToken, bool dead, string surveyStatus, string? error, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var updated = await connection.ExecuteAsync(
            """
            UPDATE outbox_message
               SET status = :status, total_attempts = total_attempts + :attempted, token_ct = NULL,
                   last_error = :error, lease_until = NULL
             WHERE message_id = :MessageId AND claim_token = :token AND status = 'IN_FLIGHT'
            """, new { status = dead ? "DEAD" : "CANCELLED", attempted = dead ? 1 : 0, error, m.MessageId, token = claimToken.ToByteArray() }, tx);
        if (updated == 1)
        {
            await connection.ExecuteAsync(
                "UPDATE survey SET status = :surveyStatus, token_hash = NULL WHERE survey_id = :SurveyId AND status IN ('SCHEDULED', 'SENT')",
                new { surveyStatus, m.SurveyId }, tx);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>Records a provider-reported opt-out (the recipient replied STOP earlier, or the provider blocks them).</summary>
    public async Task RecordOptOutAsync(string contactId, Channel channel, string actor, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await ContactRepository.OptOutAsync(connection, tx, contactId, [channel], actor, "provider-opt-out", now);
        await tx.CommitAsync(ct);
    }
}
