namespace MailSink;

/// <summary>
/// A message as it came off the wire, free of any SmtpServer types so the capture pipeline
/// can be exercised without an SMTP session.
/// </summary>
public sealed record IncomingMessage(
    byte[] Raw,
    string From,
    IReadOnlyList<string> To,
    string? ClientAddress = null);

public sealed record CaptureResult(bool Succeeded, string? Location, string? Error)
{
    public static CaptureResult Stored(string location) => new(true, location, null);

    public static CaptureResult Failed(string error) => new(false, null, error);
}

public interface IMailCapture
{
    Task<CaptureResult> CaptureAsync(IncomingMessage message, CancellationToken cancellationToken);
}
