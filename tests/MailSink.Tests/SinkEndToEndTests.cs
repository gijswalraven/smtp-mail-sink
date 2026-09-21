using System.Net;
using System.Net.Mail;
using MimeKit;

namespace MailSink.Tests;

/// <summary>
/// Boots the real listener on a free port against a temporary folder and sends through
/// System.Net.Mail, so the SmtpServer wiring, capture pipeline and file writer are all exercised.
/// </summary>
public sealed class SinkEndToEndTests : IAsyncLifetime
{
    private TestSink _sink = null!;

    public async Task InitializeAsync() => _sink = await TestSink.StartAsync();

    public async Task DisposeAsync() => await _sink.DisposeAsync();

    [Fact]
    public async Task A_sent_message_is_written_as_a_readable_eml_file()
    {
        using var message = new MailMessage(
            from: new MailAddress("webshop@example.test", "Example Webshop"),
            to: new MailAddress("gijs@example.test"))
        {
            Subject = "Factuur 1234",
            Body = "Plain text body.",
        };
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            "<h1>Bedankt</h1>", null, "text/html"));

        _sink.Send(message);

        var path = await _sink.WaitForSingleFileAsync();

        Assert.Contains("Factuur-1234", Path.GetFileName(path));
        Assert.Equal(DateTimeOffset.Now.ToString("yyyy-MM-dd"), Path.GetFileName(Path.GetDirectoryName(path)));

        var parsed = await MimeMessage.LoadAsync(path);
        Assert.Equal("Factuur 1234", parsed.Subject);
        Assert.Equal("webshop@example.test", ((MailboxAddress)parsed.From[0]).Address);
        Assert.Equal("gijs@example.test", ((MailboxAddress)parsed.To[0]).Address);
        Assert.Equal("Plain text body.", parsed.TextBody?.Trim());
        Assert.Equal("<h1>Bedankt</h1>", parsed.HtmlBody?.Trim());
    }

    [Fact]
    public async Task An_attachment_survives_the_round_trip()
    {
        var attachmentPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():n}.txt");
        await File.WriteAllTextAsync(attachmentPath, "hello from the attachment");

        try
        {
            using var message = new MailMessage("app@example.test", "gijs@example.test", "With attachment", "body");
            message.Attachments.Add(new Attachment(attachmentPath));

            _sink.Send(message);

            var parsed = await MimeMessage.LoadAsync(await _sink.WaitForSingleFileAsync());
            var attachment = Assert.Single(parsed.Attachments.OfType<MimePart>());

            using var content = new MemoryStream();
            Assert.NotNull(attachment.Content);
            attachment.Content.DecodeTo(content);
            Assert.Equal("hello from the attachment", System.Text.Encoding.UTF8.GetString(content.ToArray()));
        }
        finally
        {
            File.Delete(attachmentPath);
        }
    }

    [Fact]
    public async Task Any_credentials_are_accepted()
    {
        using var message = new MailMessage("app@example.test", "gijs@example.test", "Authenticated", "body");

        _sink.Send(message, new NetworkCredential("whoever", "whatever"));

        var parsed = await MimeMessage.LoadAsync(await _sink.WaitForSingleFileAsync());
        Assert.Equal("Authenticated", parsed.Subject);
    }
}
