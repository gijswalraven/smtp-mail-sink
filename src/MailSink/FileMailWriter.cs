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

    public FileMailWriter(IOptions<MailSinkOptions> options, IHostEnvironment environment)
    {
        _root = options.Value.ResolveMailDirectory(environment.ContentRootPath);
        _rootPrefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_root);
    }

    public string Destination => _root;

    public async Task<string> WriteAsync(MailName name, byte[] raw, CancellationToken cancellationToken)
    {
        var directory = string.IsNullOrEmpty(name.Folder)
            ? _root
            : EnsureUnderRoot(Path.Combine(_root, name.Folder));

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
