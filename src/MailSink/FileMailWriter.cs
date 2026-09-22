using Microsoft.Extensions.Options;

namespace MailSink;

/// <summary>Writes each message to a .eml file on a local or mounted filesystem.</summary>
public sealed class FileMailWriter : IMailWriter
{
    /// <summary>Give up rather than spin forever if something keeps taking the names we pick.</summary>
    private const int MaxNameAttempts = 1000;

    /// <summary>Windows paths are case-insensitive, so a containment check must be too -- there.</summary>
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly HashSet<string> _accountFolders;
    private readonly ILogger<FileMailWriter> _logger;

    public FileMailWriter(
        IOptions<MailSinkOptions> options,
        IHostEnvironment environment,
        ILogger<FileMailWriter> logger)
    {
        _root = options.Value.ResolveMailDirectory(environment.ContentRootPath);
        _rootPrefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        _logger = logger;
        Directory.CreateDirectory(_root);

        // Ordinal-insensitive, because the sweeper matches a directory name against this and
        // Windows and Azure Files would hand it back in whatever case they stored it.
        _accountFolders = options.Value.ResolveAccounts()
            .Select(account => account.Folder)
            .Where(folder => folder.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Each account's folder is created up front rather than on its first message, so the
        // layout an operator expects is there to look at from the start, and so a name the
        // filesystem refuses fails the host instead of failing a delivery hours later.
        foreach (var folder in _accountFolders)
        {
            Directory.CreateDirectory(EnsureUnderRoot(Path.Combine(_root, folder)));
        }
    }

    public string Destination => _root;

    public async Task<string> WriteAsync(MailName name, byte[] raw, CancellationToken cancellationToken)
    {
        // Up to two segments now -- the account's folder and the date's -- and neither reaches
        // Path.Combine already joined, so a separator inside one cannot pass for a boundary
        // between them.
        var directory = name.Folders.Any()
            ? EnsureUnderRoot(Path.Combine([_root, .. name.Folders]))
            : _root;

        Directory.CreateDirectory(directory);

        // A burst of mail can share the same millisecond, and two sessions can race for the same
        // name, so let the file system decide the winner: CreateNew fails for whoever lost and that
        // caller moves on. Testing File.Exists first leaves a window in which both callers believe
        // the name is free and one silently overwrites the other's message.
        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var path = EnsureUnderRoot(
                Path.Combine(directory, (attempt == 1 ? name : name.WithAttempt(attempt)).FileName));

            FileStream file;
            try
            {
                file = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Taken between our attempt and the create; try the next name. Any other
                // IOException (a full disk, a vanished share) is a real failure and propagates.
                continue;
            }

            await using (file)
            {
                await file.WriteAsync(raw, cancellationToken);
            }

            return path;
        }

        throw new IOException($"Could not find a free file name for '{name.RelativePath}' in '{directory}'.");
    }

    /// <summary>
    /// One pass over the mail directory, deleting the .eml files last written before
    /// <paramref name="cutoff"/> and dropping the folders they leave behind.
    /// </summary>
    /// <remarks>
    /// Age comes from the file's last write time rather than from its name. The name is built from
    /// the receive time in local time and can be turned off entirely by MailSink:GroupByDate, while
    /// the timestamp is there whatever the naming does, and is what an operator sees in the
    /// directory listing they are comparing against.
    /// </remarks>
    public Task<SweepResult> SweepAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        // Synchronous on purpose: the enumeration and the deletes are filesystem calls with no
        // async form worth the machinery, and MailRetentionService already yields to the thread
        // pool before its first sweep so a large share cannot hold up the listener.
        Task.FromResult(Sweep(cutoff.UtcDateTime));

    private SweepResult Sweep(DateTime cutoff)
    {
        string[] paths;
        try
        {
            // Materialised rather than streamed: deleting from the tree that is being enumerated
            // is what an enumerator copes with least well, and the array holds only names.
            paths = Directory.Exists(_root)
                ? Directory.GetFiles(_root, "*.eml", SearchOption.AllDirectories)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "retention: could not list {Root}, skipping this sweep", _root);
            return new SweepResult(0, 0);
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
                _logger.LogWarning(ex, "retention: could not delete {Path}", path);
            }
        }

        return new SweepResult(files, RemoveEmptyFolders());
    }

    /// <summary>
    /// Removes the folders the deleted files leave behind, so a long-running sink does not collect
    /// a year of empty date folders. The mail directory itself stays, and so does every folder an
    /// account writes to: those are created at startup on purpose, and an empty one tells an
    /// operator that the account exists and has had no mail.
    /// </summary>
    private int RemoveEmptyFolders()
    {
        string[] directories;
        try
        {
            directories = Directory.Exists(_root)
                ? Directory.GetDirectories(_root, "*", SearchOption.AllDirectories)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "retention: could not list the folders under {Root}", _root);
            return 0;
        }

        var removed = 0;

        // Deepest first, so an account folder holding one empty date folder is reconsidered once
        // that date folder is gone. A child path is always longer than its parent, which is all
        // the ordering this needs.
        foreach (var directory in directories.OrderByDescending(path => path.Length))
        {
            // Matched on the name rather than the full path: an account folder is a single segment
            // directly under the mail directory, and MailSinkOptions.Validate has already refused
            // two that differ only in case, so this cannot mistake one folder for another.
            if (_accountFolders.Contains(Path.GetFileName(directory)))
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
                _logger.LogDebug(ex, "retention: could not remove {Directory}", directory);
            }
        }

        return removed;
    }

    /// <summary>
    /// Resolves a path and refuses it if it left the mail directory. MailNaming already strips
    /// every separator, so nothing should ever reach this -- which is the point: it is the check
    /// that has to hold if that sanitising is ever weakened or bypassed.
    /// </summary>
    private string EnsureUnderRoot(string path)
    {
        var resolved = Path.GetFullPath(path);

        if (!resolved.StartsWith(_rootPrefix, PathComparison))
        {
            throw new InvalidOperationException(
                $"Refusing to write '{resolved}': it resolves outside the mail directory '{_root}'.");
        }

        return resolved;
    }
}
