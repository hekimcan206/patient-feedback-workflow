using System.Diagnostics;
using System.Globalization;
using System.Text;
using PatientFeedback.Security;

namespace PatientFeedback.UnitTests.Security;

public sealed class WebhookVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly WebhookSecret Current = WebhookSecret.Parse(WebhookSecret.Generate());
    private static readonly WebhookSecret Previous = WebhookSecret.Parse(WebhookSecret.Generate());
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"type":"visit.completed","visitId":"V1"}""");

    private static WebhookVerifier Verifier(params WebhookSecret[] secrets) => new(secrets.Length == 0 ? [Current] : secrets, new FixedClock(Now));

    private static WebhookHeaders Signed(byte[] body, long timestamp, params WebhookSecret[] signers)
    {
        const string id = "msg_2Kp9";
        var signature = new WebhookSigner(signers.Length == 0 ? [Current] : signers).Sign(id, timestamp, body);
        return new WebhookHeaders(id, timestamp.ToString(CultureInfo.InvariantCulture), signature);
    }

    [Fact]
    public void Accepts_a_fresh_correctly_signed_delivery()
    {
        var result = Verifier().Verify(Signed(Body, Now.ToUnixTimeSeconds()), Body);

        result.Verdict.Should().Be(WebhookVerdict.Valid);
        result.MessageId.Should().Be("msg_2Kp9");
    }

    [Fact]
    public void A_single_changed_body_byte_invalidates_the_signature()
    {
        var headers = Signed(Body, Now.ToUnixTimeSeconds());
        var tampered = (byte[])Body.Clone();
        tampered[^3] ^= 0x01;

        Verifier().Verify(headers, tampered).Verdict.Should().Be(WebhookVerdict.InvalidSignature);
    }

    [Fact]
    public void The_message_id_and_timestamp_are_part_of_the_signed_content()
    {
        var headers = Signed(Body, Now.ToUnixTimeSeconds());

        Verifier().Verify(headers with { Id = "msg_other" }, Body).Verdict.Should().Be(WebhookVerdict.InvalidSignature);
        Verifier().Verify(headers with { Timestamp = (Now.ToUnixTimeSeconds() - 1).ToString(CultureInfo.InvariantCulture) }, Body)
            .Verdict.Should().Be(WebhookVerdict.InvalidSignature);
    }

    [Fact]
    public void A_signature_from_an_unknown_secret_is_rejected() =>
        Verifier().Verify(Signed(Body, Now.ToUnixTimeSeconds(), Previous), Body).Verdict.Should().Be(WebhookVerdict.InvalidSignature);

    [Theory]
    [InlineData(-301, WebhookVerdict.TimestampTooOld)]
    [InlineData(-300, WebhookVerdict.Valid)]
    [InlineData(300, WebhookVerdict.Valid)]
    [InlineData(301, WebhookVerdict.TimestampInFuture)]
    public void Replay_window_is_five_minutes_in_both_directions(int offsetSeconds, WebhookVerdict expected)
    {
        var timestamp = Now.ToUnixTimeSeconds() + offsetSeconds;

        Verifier().Verify(Signed(Body, timestamp), Body).Verdict.Should().Be(expected);
    }

    [Fact]
    public void A_captured_delivery_replayed_an_hour_later_is_rejected_even_with_a_valid_signature()
    {
        var captured = Signed(Body, Now.ToUnixTimeSeconds());
        var later = new WebhookVerifier([Current], new FixedClock(Now.AddHours(1)));

        later.Verify(captured, Body).Verdict.Should().Be(WebhookVerdict.TimestampTooOld);
    }

    [Theory]
    [InlineData(null, "1", "v1,x")]
    [InlineData("id", null, "v1,x")]
    [InlineData("id", "1", null)]
    [InlineData("", "1", "v1,x")]
    public void Missing_headers_are_rejected(string? id, string? timestamp, string? signature) =>
        Verifier().Verify(new WebhookHeaders(id, timestamp, signature), Body).Verdict.Should().Be(WebhookVerdict.MissingHeaders);

    [Theory]
    [InlineData("12.5")]
    [InlineData("-5")]
    [InlineData("1e9")]
    [InlineData("now")]
    public void Malformed_timestamps_are_rejected(string timestamp) =>
        Verifier().Verify(new WebhookHeaders("id", timestamp, "v1,AAAA"), Body).Verdict.Should().Be(WebhookVerdict.MalformedTimestamp);

    [Theory]
    [InlineData("v2,AAAA")]
    [InlineData("v1,not-base64!")]
    [InlineData("v1,AAAA")]
    [InlineData("v1a,AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void Unknown_versions_wrong_lengths_and_garbage_never_match(string signature) =>
        Verifier().Verify(new WebhookHeaders("id", Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), signature), Body)
            .Verdict.Should().Be(WebhookVerdict.InvalidSignature);

    [Fact]
    public void Rotation_sender_signs_with_both_keys_and_old_and_new_receivers_accept()
    {
        // Step 1 of a rotation: the sender signs with the new and the old secret (two space-separated signatures).
        var headers = Signed(Body, Now.ToUnixTimeSeconds(), Current, Previous);

        Verifier(Previous).Verify(headers, Body).IsValid.Should().BeTrue("a receiver still on the old secret keeps working");
        Verifier(Current).Verify(headers, Body).IsValid.Should().BeTrue("a receiver already on the new secret works");
    }

    [Fact]
    public void Rotation_receiver_accepts_both_secrets_during_the_overlap()
    {
        var receiver = Verifier(Current, Previous);

        receiver.Verify(Signed(Body, Now.ToUnixTimeSeconds(), Previous), Body).IsValid.Should().BeTrue();
        receiver.Verify(Signed(Body, Now.ToUnixTimeSeconds(), Current), Body).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("whsec_c2hvcnQ=")]
    [InlineData("whsec_!!!")]
    public void Weak_or_malformed_secrets_are_refused_at_startup(string secret) =>
        FluentActions.Invoking(() => WebhookSecret.Parse(secret)).Should().Throw<FormatException>();

    /// <summary>
    /// Timing side channel: with a naive byte-by-byte compare, a signature whose first byte is right takes longer to
    /// reject than one whose first byte is wrong, which lets an attacker recover the MAC byte by byte. With a
    /// fixed-time compare, rejecting "wrong at byte 0" and "wrong at byte 31" takes the same time. Medians of
    /// interleaved rounds keep the test stable on a noisy CI runner; the bound is loose on purpose (it guards against
    /// an early-exit compare, which shows up as a large, systematic gap, not against nanosecond noise).
    /// </summary>
    [Fact]
    public void Rejection_time_does_not_depend_on_how_many_leading_bytes_match()
    {
        var verifier = Verifier();
        var timestamp = Now.ToUnixTimeSeconds();
        var good = Signed(Body, timestamp);
        var mac = Convert.FromBase64String(good.Signature![3..]);
        var wrongFirst = (byte[])mac.Clone();
        wrongFirst[0] ^= 0xFF;
        var wrongLast = (byte[])mac.Clone();
        wrongLast[^1] ^= 0xFF;
        var first = good with { Signature = "v1," + Convert.ToBase64String(wrongFirst) };
        var last = good with { Signature = "v1," + Convert.ToBase64String(wrongLast) };

        const int rounds = 21, perRound = 3000;
        var firstTimes = new List<double>(rounds);
        var lastTimes = new List<double>(rounds);
        for (var i = 0; i < 2000; i++)
        {
            verifier.Verify(first, Body);
            verifier.Verify(last, Body);
        }

        for (var r = 0; r < rounds; r++)
        {
            firstTimes.Add(Measure(() => verifier.Verify(first, Body)));
            lastTimes.Add(Measure(() => verifier.Verify(last, Body)));
        }

        var ratio = Median(lastTimes) / Median(firstTimes);
        ratio.Should().BeInRange(0.7, 1.4);

        static double Measure(Action verify)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < perRound; i++)
            {
                verify();
            }

            return sw.Elapsed.TotalMilliseconds;
        }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
