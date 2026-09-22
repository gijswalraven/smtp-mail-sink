using Microsoft.Extensions.Options;

namespace MailSink;

/// <summary>
/// Deletes captured .eml files once they are older than <see cref="RetentionOptions.MaxAge"/>, and
/// drops the date folders they leave behind.
/// </summary>
/// <remarks>
/// <para>
/// The sink is the only process that has to be running for this to happen, and it already has the
/// mail directory open: in the container that directory is the mounted Azure Files share, so the
/// same sweep covers the share without a storage key, a scheduled task, or a second machine that
/// has to be awake at the right moment.
/// </para>
/// <para>
/// Age comes from the file's last write time rather than from its name. The name is built from the
/// receive time in local time and can be turned off entirely by MailSink:GroupByDate, while the
/// timestamp is there whatever the naming does, and is what an operator sees in the directory
/// listing they are comparing against.
/// </para>
/// </remarks>
public sealed class MailRetentionService(
    IOptions<MailSinkOptions> options,
    IHostEnvironment environment,
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
                "age such as 7.00:00:00 to have the sink delete .eml files older than that.");
            return;
        }

        var root = _options.ResolveMailDirectory(environment.ContentRootPath);

        logger.LogInformation(
            "retention: deleting .eml files under {Root} older than {MaxAge}, sweeping every {Interval}",
            root,
            retention.MaxAge,
            retention.SweepInterval);

        // Hand the rest of the method to the thread pool before the first sweep, so a large share
        // cannot hold up the SMTP listener: everything a BackgroundService does before its first
        // await runs inside the host's StartAsync.
        await Task.Yield();

        // The timer takes the TimeProvider so a test can advance the clock rather than wait an hour.
        using var timer = new PeriodicTimer(retention.SweepInterval, timeProvider);

        do
        {
            Sweep(root);
        }
        while (await WaitForNextSweepAsync(timer, stoppingToken));
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

    /// <summary>
    /// One pass over the mail directory. Returns what it removed, and throws nothing: a single
    /// locked file, or a share that went away for a moment, must not end the sweeper for the rest
    /// of the process's life, and whatever it could not delete this hour it retries the next.
    /// </summary>
    internal (int Files, int Folders) Sweep(string root)
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - _options.Retention.MaxAge;

        string[] paths;
        try
        {
            // Materialised rather than streamed: deleting from the tree that is being enumerated
            // is what an enumerator copes with least well, and the array holds only names.
            paths = Directory.Exists(root)
                ? Directory.GetFiles(root, "*.eml", SearchOption.AllDirectories)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "retention: could not list {Root}, skipping this sweep", root);
            return (0, 0);
        }

        var files = 0;

        foreach (var path in paths)
        {
            // A file that vanished between the listing and here reports 1601 and so reads as
            // stale; the delete that follows then finds nothing and returns without throwing.
            // Asking the filesystem twice cannot be avoided, and this is the harmless side of it.
            if (File.GetLastWriteTimeUtc(path) >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                files++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "retention: could not delete {Path}", path);
            }
        }

        var folders = RemoveEmptyFolders(root);

        if (files > 0 || folders > 0)
        {
            logger.LogInformation(
                "retention: deleted {Files} file(s) last written before {Cutoff:u} and {Folders} empty folder(s)",
                files,
                cutoff,
                folders);
        }
        else
        {
            logger.LogDebug("retention: nothing older than {Cutoff:u}", cutoff);
        }

        return (files, folders);
    }

    /// <summary>
    /// Removes the folders the deleted files leave behind, so a long-running sink does not collect
    /// a year of empty date folders. The mail directory itself stays, and so does every folder an
    /// account writes to: those are created at startup on purpose, and an empty one tells an
    /// operator that the account exists and has had no mail.
    /// </summary>
    private int RemoveEmptyFolders(string root)
    {
        string[] directories;
        try
        {
            directories = Directory.Exists(root)
                ? Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "retention: could not list the folders under {Root}", root);
            return 0;
        }

        var accountFolders = _options.ResolveAccounts()
            .Select(account => account.Folder)
            .Where(folder => folder.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = 0;

        // Deepest first, so an account folder holding one empty date folder is reconsidered once
        // that date folder is gone. A child path is always longer than its parent, which is all
        // the ordering this needs.
        foreach (var directory in directories.OrderByDescending(path => path.Length))
        {
            // Matched on the name rather than the full path: an account folder is a single segment
            // directly under the mail directory, and MailSinkOptions.Validate has already refused
            // two that differ only in case, so this cannot mistake one folder for another.
            if (accountFolders.Contains(Path.GetFileName(directory)))
            {
                continue;
            }

            try
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    continue;
                }

                Directory.Delete(directory);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Usually a message that arrived between the check and the delete. Debug rather
                // than a warning: an empty folder that survives until the next sweep costs nothing.
                logger.LogDebug(ex, "retention: could not remove {Directory}", directory);
            }
        }

        return removed;
    }
}
