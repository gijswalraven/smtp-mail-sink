using Microsoft.Extensions.Options;

namespace MailSink;

/// <summary>
/// Names an incoming message and hands it to the writer. This is where the interesting
/// behaviour lives, which is why it takes a plain <see cref="IncomingMessage"/>.
/// </summary>
public sealed class MailCapture(
    IOptions<MailSinkOptions> options,
    IMailWriter writer,
    IMessageMetadataReader metadataReader,
    TimeProvider timeProvider,
    ILogger<MailCapture> logger) : IMailCapture
{
    private readonly MailSinkOptions _options = options.Value;

    public async Task<CaptureResult> CaptureAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        var metadata = metadataReader.Read(message.Raw);
        var name = MailNaming.Build(
            timeProvider.GetLocalNow(),
            message.From,
            message.To,
            metadata.Subject,
            _options.GroupByDate);

        if (!metadata.Parsed)
        {
            logger.LogWarning("Message from {From} could not be parsed as MIME; storing it unnamed", message.From);
        }

        string location;
        try
        {
            location = await writer.WriteAsync(name, message.Raw, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not store message from {From} as {Name}", message.From, name.RelativePath);
            return CaptureResult.Failed(ex.Message);
        }

        logger.LogInformation(
            "Saved {Bytes} bytes from {From} to {To} -> {Location}",
            message.Raw.Length,
            message.From,
            string.Join(", ", message.To),
            location);

        return CaptureResult.Stored(location);
    }
}
