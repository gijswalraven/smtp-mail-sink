namespace MailSink;

/// <summary>Stores a captured message. Swap the implementation to change where mail ends up.</summary>
public interface IMailWriter
{
    /// <summary>Human-readable description of the destination, for the startup log.</summary>
    string Destination { get; }

    /// <summary>Writes the raw message and returns the location it ended up at.</summary>
    Task<string> WriteAsync(MailName name, byte[] raw, CancellationToken cancellationToken);
}
