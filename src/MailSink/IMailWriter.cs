namespace MailSink;

/// <summary>What a retention sweep removed.</summary>
/// <param name="Messages">Stored messages deleted.</param>
/// <param name="Folders">
/// Empty folders dropped with them. Always 0 where the destination has no real folders: a blob
/// container's are only the prefixes of the blobs in it, so they disappear on their own.
/// </param>
public readonly record struct SweepResult(int Messages, int Folders);

/// <summary>
/// Stores a captured message. Swap the implementation to change where mail ends up.
/// </summary>
/// <remarks>
/// Expiring stored mail belongs here too, rather than in the service that schedules it: only the
/// implementation knows what a stored message is -- a file under a directory, or a blob under a
/// prefix -- and pairing the two in one type keeps a destination from being written to by one
/// half of a mismatched pair and swept by the other.
/// </remarks>
public interface IMailWriter
{
    /// <summary>Human-readable description of the destination, for the startup log.</summary>
    string Destination { get; }

    /// <summary>Writes the raw message and returns the location it ended up at.</summary>
    Task<string> WriteAsync(MailName name, byte[] raw, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes stored messages last written before <paramref name="cutoff"/>, and reports what it
    /// removed. Throws nothing for a message it could not delete: one locked file, or a destination
    /// that was away for a moment, must not end the sweeper for the rest of the process's life,
    /// and whatever it misses this hour it retries the next.
    /// </summary>
    Task<SweepResult> SweepAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
