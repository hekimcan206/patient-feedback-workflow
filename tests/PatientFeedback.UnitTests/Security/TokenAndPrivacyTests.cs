using System.Security.Cryptography;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using PatientFeedback.Domain;
using PatientFeedback.Privacy;
using PatientFeedback.Security;

namespace PatientFeedback.UnitTests.Security;

public sealed class SurveyTokenTests
{
    [Fact]
    public void Tokens_are_url_safe_43_characters_and_unique()
    {
        var tokens = Enumerable.Range(0, 2000).Select(_ => SurveyTokens.Issue()).ToList();

        tokens.Select(t => t.Value).Should().OnlyHaveUniqueItems();
        tokens.Should().AllSatisfy(t =>
        {
            t.Value.Should().HaveLength(43).And.MatchRegex("^[A-Za-z0-9_-]+$");
            t.Hash.Should().HaveCount(32);
        });
    }

    [Fact]
    public void Only_the_hash_identifies_a_token_and_it_is_reproducible_from_the_link()
    {
        var token = SurveyTokens.Issue();

        SurveyTokens.TryHash(token.Value, out var hash).Should().BeTrue();
        hash.Should().Equal(token.Hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("../../../../etc/passwd/../../../../etc/passwd")]
    public void Malformed_tokens_are_rejected_before_any_database_lookup(string? token) =>
        SurveyTokens.TryHash(token, out _).Should().BeFalse();
}

public sealed class PiiProtectorTests
{
    private static readonly KeyEncryptionKey Kek1 = new("kek-1", RandomNumberGenerator.GetBytes(32));
    private static readonly KeyEncryptionKey Kek2 = new("kek-2", RandomNumberGenerator.GetBytes(32));

    [Property]
    public Property Any_text_round_trips_through_envelope_encryption() =>
        Prop.ForAll(ArbMap.Default.ArbFor<NonNull<string>>(), text =>
        {
            var protector = new PiiProtector([Kek1], "kek-1");
            var (key, wrapped) = protector.CreateDataKey("c1");
            var ct = PiiProtector.Encrypt(key, "c1", "phone", text.Get);
            using var again = protector.Unwrap(wrapped, "c1");
            return PiiProtector.Decrypt(again, "c1", "phone", ct) == text.Get;
        });

    [Fact]
    public void Same_plaintext_encrypts_differently_each_time()
    {
        var (key, _) = new PiiProtector([Kek1], "kek-1").CreateDataKey("c1");

        PiiProtector.Encrypt(key, "c1", "phone", "+905320001234").Should().NotEqual(PiiProtector.Encrypt(key, "c1", "phone", "+905320001234"));
    }

    [Fact]
    public void Ciphertext_moved_to_another_field_or_record_fails_authentication()
    {
        var protector = new PiiProtector([Kek1], "kek-1");
        var (key, _) = protector.CreateDataKey("c1");
        var phone = PiiProtector.Encrypt(key, "c1", "phone", "+905320001234");

        FluentActions.Invoking(() => PiiProtector.Decrypt(key, "c1", "full_name", phone)).Should().Throw<CryptographicException>();
        FluentActions.Invoking(() => PiiProtector.Decrypt(key, "c2", "phone", phone)).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void A_wrapped_key_cannot_be_unwrapped_for_another_record_or_without_its_kek()
    {
        var protector = new PiiProtector([Kek1], "kek-1");
        var (_, wrapped) = protector.CreateDataKey("c1");

        FluentActions.Invoking(() => protector.Unwrap(wrapped, "c2")).Should().Throw<CryptographicException>();
        FluentActions.Invoking(() => new PiiProtector([Kek2], "kek-2").Unwrap(wrapped, "c1")).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Flipped_ciphertext_bit_is_detected()
    {
        var (key, _) = new PiiProtector([Kek1], "kek-1").CreateDataKey("c1");
        var ct = PiiProtector.Encrypt(key, "c1", "phone", "+905320001234");
        ct[^1] ^= 1;

        FluentActions.Invoking(() => PiiProtector.Decrypt(key, "c1", "phone", ct)).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Rewrap_moves_a_data_key_to_the_new_kek_and_field_ciphertext_still_decrypts()
    {
        var old = new PiiProtector([Kek1], "kek-1");
        var (key, wrapped) = old.CreateDataKey("c1");
        var ct = PiiProtector.Encrypt(key, "c1", "phone", "+905320001234");
        var rotated = new PiiProtector([Kek1, Kek2], "kek-2");

        var rewrapped = rotated.Rewrap(wrapped, "c1");

        rewrapped.KekId.Should().Be("kek-2");
        using var unwrapped = new PiiProtector([Kek2], "kek-2").Unwrap(rewrapped, "c1");
        PiiProtector.Decrypt(unwrapped, "c1", "phone", ct).Should().Be("+905320001234");
    }

    [Fact]
    public void Blind_index_is_deterministic_normalised_and_key_dependent()
    {
        var index = new BlindIndex(RandomNumberGenerator.GetBytes(32));

        index.Compute(" p123 ").Should().Equal(index.Compute("P123"));
        index.Compute("P123").Should().NotEqual(index.Compute("P124"));
        new BlindIndex(RandomNumberGenerator.GetBytes(32)).Compute("P123").Should().NotEqual(index.Compute("P123"));
    }

    [Fact]
    public void Masks_keep_only_what_a_handler_needs_to_recognise_a_contact()
    {
        PiiMask.Phone("+905321234567").Should().Be("********4567");
        PiiMask.Name("Ayşe Yılmaz").Should().Be("A*** Y*****");
    }
}

public sealed class VisitEventValidationTests
{
    private static VisitCompletedEvent Valid() => new()
    {
        Type = VisitCompletedEvent.EventType,
        VisitId = "V1",
        PatientRef = "P1",
        PatientName = "Ayşe Yılmaz",
        Phone = "+905320001234",
        Department = "Cardiology",
        CompletedAt = DateTimeOffset.UnixEpoch,
        SurveyConsent = true,
    };

    [Fact]
    public void A_complete_event_is_valid() => Valid().Validate().Should().BeEmpty();

    [Theory]
    [InlineData("05320001234")]
    [InlineData("+0532")]
    [InlineData("+90 532 000 12 34")]
    [InlineData("+9053200012345678")]
    public void Phone_must_be_e164(string phone) => (Valid() with { Phone = phone }).Validate().Should().ContainSingle();

    [Fact]
    public void Wrong_type_and_oversized_fields_are_reported_together() =>
        (Valid() with { Type = "visit.started", Department = new string('x', 65) }).Validate().Should().HaveCount(2);
}
