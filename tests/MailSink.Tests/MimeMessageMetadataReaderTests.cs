using System.Text;
using MailSink;

namespace MailSink.Tests;

public class MimeMessageMetadataReaderTests
{
    private static readonly MimeMessageMetadataReader Reader = new();

    private static byte[] Raw(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Read_returns_a_plain_subject()
    {
        var metadata = Reader.Read(Raw("Subject: Order confirmed\r\nFrom: a@b.test\r\n\r\nbody"));

        Assert.True(metadata.Parsed);
        Assert.Equal("Order confirmed", metadata.Subject);
    }

    [Fact]
    public void Read_decodes_an_rfc2047_encoded_subject()
    {
        // =?utf-8?B?V2FhcnNjaHV3aW5nOiBjYWbDqSBzZXJ2ZXVy?= is "Waarschuwing: café serveur".
        var metadata = Reader.Read(Raw(
            "Subject: =?utf-8?B?V2FhcnNjaHV3aW5nOiBjYWbDqSBzZXJ2ZXVy?=\r\nFrom: a@b.test\r\n\r\nbody"));

        Assert.True(metadata.Parsed);
        Assert.Equal("Waarschuwing: café serveur", metadata.Subject);
    }

    [Fact]
    public void Read_returns_no_subject_when_the_header_is_absent()
    {
        var metadata = Reader.Read(Raw("From: a@b.test\r\n\r\nbody"));

        Assert.True(metadata.Parsed);
        Assert.Null(metadata.Subject);
    }

    [Fact]
    public void Read_treats_empty_input_as_a_message_with_no_headers()
    {
        var metadata = Reader.Read([]);

        Assert.True(metadata.Parsed);
        Assert.Null(metadata.Subject);
    }

    [Theory]
    [InlineData("this is not a mime message at all")]
    [InlineData("Subject without a colon\r\n\r\nbody")]
    public void Read_reports_unparsed_when_the_headers_are_malformed(string text)
    {
        // MimeKit rejects a header line with no colon. The sink still stores these bytes; they
        // just lose their subject in the file name. MailCaptureTests covers that behaviour.
        var metadata = Reader.Read(Raw(text));

        Assert.False(metadata.Parsed);
        Assert.Null(metadata.Subject);
    }
}
