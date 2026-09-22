using Microsoft.Extensions.Options;

namespace MailSink;

/// <summary>
/// Asks the writer to delete captured mail once it is older than
/// <see cref="RetentionOptions.MaxAge"/>, at startup and then on a timer.
/// </summary>
/// <remarks>
/// <para>
/// The sink is the only process that has to be running for this to happen, and it already has its
/// destination open: in the container that destination is the blob container the mail was written
/// to, so the same sweep covers it without a storage key, a lifecycle rule to keep in step with
/// the configuration, a scheduled task, or a second machine that has to be awake at the right
/// moment.
/// </para>
/// <para>
/// What a stored message is, and what age it has, is the writer's business --
/// <see cref="IMailWriter.SweepAsync"/>. This type owns only when a sweep happens and what gets
/// said about it afterwards.
/// </para>
/// </remarks>
public sealed class MailRetentionService(
    IOptions<MailSinkOptions> options,
    IMailWriter writer,
    TimeProvider timeProvider,
    ILogger<MailRetentionService> logger) : BackgroundService
{
    private readonly MailSinkOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = _options.Retention;

        if (!retention.IsEnabled)
        {
            logger.LogInformation(
                "retention: off, captured mail is kept forever. Set MailSink:Retention:MaxAge to an " +
                "age such as 7.00:00:00 to have the sink delete messages older than that.");
            return;
        }

        logger.LogInformation(
            "retention: deleting mail in {Destination} older than {MaxAge}, sweeping every {Interval}",
            writer.Destination,
            retention.MaxAge,
            retention.SweepInterval);

        // Hand the rest of the method to the thread pool before the first sweep, so a large
        // destination cannot hold up the SMTP listener: everything a BackgroundService does before
        // its first await runs inside the host's StartAsync.
        await Task.Yield();

        // The timer takes the TimeProvider so a test can advance the clock rather than wait an hour.
        using var timer = new PeriodicTimer(retention.SweepInterval, timeProvider);

        do
        {
            await SweepAsync(stoppingToken);
        }
        while (await WaitForNextSweepAsync(timer, stoppingToken));
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        var cutoff = timeProvider.GetUtcNow() - _options.Retention.MaxAge;

        SweepResult result;
        try
        {
            result = await writer.SweepAsync(cutoff, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown landed mid-sweep. Whatever was not reached this time is still expired on
            // the next start, which begins with a sweep of its own.
            return;
        }

        if (result.Folders > 0)
        {
            logger.LogInformation(
                "retention: deleted {Messages} message(s) last written before {Cutoff:u} and {Folders} empty folder(s)",
                result.Messages,
                cutoff,
                result.Folders);
        }
        else if (result.Messages > 0)
        {
            logger.LogInformation(
                "retention: deleted {Messages} message(s) last written before {Cutoff:u}",
                result.Messages,
                cutoff);
        }
        else
        {
            logger.LogDebug("retention: nothing older than {Cutoff:u}", cutoff);
        }
    }

    private static async Task<bool> WaitForNextSweepAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
            return false;
        }
    }
}
