using MailSink;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MailSink.Tests;

/// <summary>
/// The scheduling half of retention: when a sweep happens and what age it asks for. What a stored
/// message is, and which ones survive, belongs to the writer -- see
/// <see cref="FileMailWriterTests"/>.
/// </summary>
public class MailRetentionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);

    private readonly InMemoryMailWriter _writer = new();

    private MailRetentionService Create(Action<MailSinkOptions>? configure = null)
    {
        var options = TestOptions.For(settings =>
        {
            settings.Retention.MaxAge = TimeSpan.FromHours(24);
            configure?.Invoke(settings);
        });

        return new MailRetentionService(
            options,
            _writer,
            _time,
            NullLogger<MailRetentionService>.Instance);
    }

    [Fact]
    public async Task Sweeps_at_startup_and_again_each_interval()
    {
        var service = Create(settings => settings.Retention.SweepInterval = TimeSpan.FromHours(1));
        await service.StartAsync(CancellationToken.None);

        // A sweep at startup, so a sink that was off over the weekend clears what expired while it
        // was down rather than waiting an interval first.
        await WaitUntil(() => _writer.Sweeps.Count >= 1);
        Assert.Equal(Now - TimeSpan.FromHours(24), _writer.Sweeps[0]);

        _time.Advance(TimeSpan.FromHours(1));

        // The second cutoff has moved with the clock: the sweeper asks for an age, not for a fixed
        // moment it worked out once at startup.
        await WaitUntil(() => _writer.Sweeps.Count >= 2);
        Assert.Equal(Now - TimeSpan.FromHours(23), _writer.Sweeps[1]);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweeps_nothing_when_no_age_is_configured()
    {
        var service = Create(settings => settings.Retention.MaxAge = TimeSpan.Zero);

        await service.StartAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromDays(30));
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(_writer.Sweeps);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(24, 0)]
    public void Validate_refuses_retention_that_cannot_do_what_it_says(int maxAgeHours, int intervalHours)
    {
        var options = new MailSinkOptions
        {
            Retention =
            {
                MaxAge = TimeSpan.FromHours(maxAgeHours),
                SweepInterval = TimeSpan.FromHours(intervalHours),
            },
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));
    }

    /// <summary>
    /// The sweeps run on the thread pool, so the assertions have to wait for one rather than
    /// assume it already happened.
    /// </summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException("The sweeper never got to it.");
    }
}
