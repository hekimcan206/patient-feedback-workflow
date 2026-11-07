using Microsoft.Extensions.Options;
using PatientFeedback.Dispatch;
using PatientFeedback.UnitTests.Security;
using PatientFeedback.Web.Dispatch;
using PatientFeedback.Web.Infrastructure;

namespace PatientFeedback.UnitTests.Dispatch;

public sealed class DispatchScheduleTests
{
    private static readonly DispatchOptions Options = new() { SendDelay = TimeSpan.FromHours(2) };

    // Istanbul is UTC+3 all year.
    private static DateTimeOffset Istanbul(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Survey_goes_out_after_the_configured_delay()
    {
        var at = new DispatchSchedule(Options).SendAt(Istanbul(10, 11), Istanbul(10, 11, 5));

        at.Should().Be(Istanbul(10, 13));
    }

    [Fact]
    public void Evening_visit_is_held_until_quiet_hours_end_next_morning()
    {
        var at = new DispatchSchedule(Options).SendAt(Istanbul(10, 20), Istanbul(10, 20));

        at.Should().Be(Istanbul(11, 9));
    }

    [Fact]
    public void Early_morning_falls_to_nine_the_same_day()
    {
        var at = new DispatchSchedule(Options).SendAt(Istanbul(10, 4), Istanbul(10, 4));

        at.Should().Be(Istanbul(10, 9));
    }

    [Fact]
    public void Late_events_are_never_scheduled_in_the_past()
    {
        var now = Istanbul(12, 15);

        new DispatchSchedule(Options).SendAt(Istanbul(10, 10), now).Should().Be(now);
    }

    [Fact]
    public void Quiet_hours_can_be_disabled()
    {
        var o = new DispatchOptions { SendDelay = TimeSpan.Zero, QuietHoursEnabled = false };

        new DispatchSchedule(o).SendAt(Istanbul(10, 23), Istanbul(10, 23)).Should().Be(Istanbul(10, 23));
    }
}

public sealed class RetryAndPlannerTests
{
    private static readonly DispatchOptions Options = new() { RetryBaseDelay = TimeSpan.FromSeconds(2), RetryMaxDelay = TimeSpan.FromMinutes(10), MaxAttemptsPerChannel = 3 };
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<Channel> Both = [Channel.WhatsApp, Channel.Sms];

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(5, 32)]
    [InlineData(20, 600)]
    public void Full_jitter_backoff_ceiling_doubles_and_is_capped(int attempt, double ceilingSeconds)
    {
        new RetryPolicy(Options, () => 1.0).Delay(attempt, null).TotalSeconds.Should().BeApproximately(ceilingSeconds, 0.001);
        new RetryPolicy(Options, () => 0.0).Delay(attempt, null).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Provider_retry_after_is_a_floor() =>
        new RetryPolicy(Options, () => 0.1).Delay(1, TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(30));

    [Fact]
    public void Route_puts_the_preferred_channel_first_and_drops_opted_out_ones()
    {
        DispatchPlanner.Route(Channel.Sms, new HashSet<Channel>()).Should().Equal(Channel.Sms, Channel.WhatsApp);
        DispatchPlanner.Route(Channel.WhatsApp, new HashSet<Channel> { Channel.WhatsApp }).Should().Equal(Channel.Sms);
        DispatchPlanner.Route(Channel.WhatsApp, new HashSet<Channel> { Channel.WhatsApp, Channel.Sms }).Should().BeEmpty();
    }

    private static DispatchPlanner Planner() => new(Options, new RetryPolicy(Options, () => 0.5));

    [Fact]
    public void Accepted_is_sent() =>
        Planner().Decide(SendResult.Accepted("wamid.1"), Channel.WhatsApp, 1, Both, Now).Should().Be(new DispatchOutcome(OutcomeKind.Sent, "wamid.1"));

    [Fact]
    public void Transient_failure_retries_on_the_same_channel_until_attempts_run_out_then_falls_back()
    {
        var planner = Planner();

        var retry = planner.Decide(SendResult.Transient("130429-rate-limit"), Channel.WhatsApp, 2, Both, Now);
        retry.Kind.Should().Be(OutcomeKind.RetryLater);
        retry.NextAttemptAt.Should().Be(Now.AddSeconds(2)); // 0.5 × 2 s × 2^(2−1)

        var exhausted = planner.Decide(SendResult.Transient("130429-rate-limit"), Channel.WhatsApp, 3, Both, Now);
        exhausted.Kind.Should().Be(OutcomeKind.FallBack);
        exhausted.NextChannel.Should().Be(Channel.Sms);
    }

    [Fact]
    public void Permanent_failure_falls_back_immediately_and_is_dead_on_the_last_channel()
    {
        Planner().Decide(SendResult.Permanent("131026-undeliverable"), Channel.WhatsApp, 1, Both, Now)
            .Should().Be(new DispatchOutcome(OutcomeKind.FallBack, "131026-undeliverable", Now, Channel.Sms));
        Planner().Decide(SendResult.Permanent("invalid-number"), Channel.Sms, 1, Both, Now).Kind.Should().Be(OutcomeKind.Dead);
    }

    [Fact]
    public void Opt_out_is_never_retried_on_that_channel()
    {
        Planner().Decide(SendResult.OptedOut("131050-user-blocked"), Channel.WhatsApp, 1, Both, Now).NextChannel.Should().Be(Channel.Sms);
        Planner().Decide(SendResult.OptedOut("operator-stop-list"), Channel.Sms, 1, [Channel.Sms], Now).Kind.Should().Be(OutcomeKind.OptedOut);
    }
}

public sealed class FakeProviderTests
{
    private static OutboundMessage Message(string phone, long id = 1) =>
        new(id, Channel.WhatsApp, phone, "visit_feedback_v1", new Dictionary<string, string> { ["department"] = "Cardiology", ["link"] = "http://x/s/t" });

    private static IOptions<FakeProviderOptions> NoRandomFailures() => Microsoft.Extensions.Options.Options.Create(new FakeProviderOptions
    {
        WhatsAppRateLimitRate = 0,
        WhatsAppTimeoutRate = 0,
        SmsServerErrorRate = 0,
        MinLatencyMs = 0,
        MaxLatencyMs = 0,
        Seed = 1,
    });

    [Theory]
    [InlineData("+905320001213", SendStatus.Permanent)]
    [InlineData("+905320001214", SendStatus.OptedOut)]
    [InlineData("+905320001200", SendStatus.Accepted)]
    public async Task WhatsApp_failure_numbers(string phone, SendStatus expected)
    {
        var provider = new FakeWhatsAppProvider(new FakeHandset(), NoRandomFailures(), new FixedClock(DateTimeOffset.UnixEpoch));

        (await provider.SendAsync(Message(phone), TestContext.Current.CancellationToken)).Status.Should().Be(expected);
    }

    [Theory]
    [InlineData("+905320001277", SendStatus.Permanent)]
    [InlineData("+905320001278", SendStatus.OptedOut)]
    [InlineData("+905320001213", SendStatus.Accepted)]
    public async Task Sms_failure_numbers(string phone, SendStatus expected)
    {
        var provider = new FakeSmsProvider(new FakeHandset(), NoRandomFailures(), new FixedClock(DateTimeOffset.UnixEpoch));

        (await provider.SendAsync(Message(phone) with { Channel = Channel.Sms }, TestContext.Current.CancellationToken)).Status.Should().Be(expected);
    }

    [Fact]
    public async Task Provider_deduplicates_by_idempotency_key_so_a_retry_never_double_delivers()
    {
        var handset = new FakeHandset();
        var provider = new FakeWhatsAppProvider(handset, NoRandomFailures(), new FixedClock(DateTimeOffset.UnixEpoch));

        var first = await provider.SendAsync(Message("+905320001200", id: 7), TestContext.Current.CancellationToken);
        var retry = await provider.SendAsync(Message("+905320001200", id: 7), TestContext.Current.CancellationToken);

        retry.ProviderMessageId.Should().Be(first.ProviderMessageId);
        handset.After(0, 10).Should().ContainSingle();
    }

    [Fact]
    public async Task Random_throttling_is_reported_as_transient_with_retry_after()
    {
        var always = Microsoft.Extensions.Options.Options.Create(new FakeProviderOptions { WhatsAppRateLimitRate = 1, MinLatencyMs = 0, MaxLatencyMs = 0 });
        var provider = new FakeWhatsAppProvider(new FakeHandset(), always, new FixedClock(DateTimeOffset.UnixEpoch));

        var result = await provider.SendAsync(Message("+905320001200"), TestContext.Current.CancellationToken);

        result.Should().Be(SendResult.Transient("130429-rate-limit", TimeSpan.FromSeconds(1)));
    }
}
