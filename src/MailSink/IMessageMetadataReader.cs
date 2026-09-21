using MimeKit;

namespace MailSink;

/// <summary>What we need from a message's headers to name its file.</summary>
/// <param name="Subject">Decoded subject, or null when absent or unreadable.</param>
/// <param name="Parsed">False when the bytes could not be read as MIME at all.</param>
public readonly record struct MessageMetadata(string? Subject, bool Parsed);

public interface IMessageMetadataReader
{
    MessageMetadata Read(byte[] raw);
}

/// <summary>
/// Reads headers with MimeKit, which decodes RFC 2047 encoded-words so a subject like
/// <c>=?utf-8?B?...?=</c> becomes readable text in the file name.
/// </summary>
public sealed class MimeMessageMetadataReader : IMessageMetadataReader
{
    public MessageMetadata Read(byte[] raw)
    {
        try
        {
            using var stream = new MemoryStream(raw, writable: false);

            // Headers only. The raw bytes are already held in memory, and building a full MIME
            // tree would hold a second, larger copy of a message that can be MaxMessageSize big;
            // concurrent senders would multiply that. The indexer returns the decoded value.
            var parser = new MimeParser(stream, MimeFormat.Entity);
            return new MessageMetadata(parser.ParseHeaders()[HeaderId.Subject], Parsed: true);
        }
        catch
        {
            // A message we cannot parse is still worth keeping; it just loses its subject.
            return new MessageMetadata(null, Parsed: false);
        }
    }
}
