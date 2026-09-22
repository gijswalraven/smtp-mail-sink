using System.Globalization;
using System.Net.Mail;
using MimeKit;

namespace MailSink.Tests;

/// <summary>
/// Boots the real listener on a free port against a temporary folder, so the SmtpServer wiring,
/// capture pipeline and file writer are all exercised. Runs in the Development mode -- plain text,
/// no credentials -- because these assert what is captured, not how the session was secured.
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

        await _sink.SendAsync(message);

        var path = await _sink.WaitForSingleFileAsync();

        Assert.Contains("Factuur-1234", Path.GetFileName(path));
        Assert.Equal(
            DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Path.GetFileName(Path.GetDirectoryName(path)));

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

            await _sink.SendAsync(message);

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
    public async Task A_hostile_subject_is_still_captured_under_a_safe_name()
    {
        using var message = new MailMessage("app@example.test", "gijs@example.test", subject: null, "body")
        {
            // An ANSI escape, a right-to-left override, and enough four-byte runes to blow the
            // 255-byte file name limit if they were counted as characters.
            Subject = "\u001b[2J‮" + string.Concat(Enumerable.Repeat("\U0001F4E7", 120)),
        };

        await _sink.SendAsync(message);

        var path = await _sink.WaitForSingleFileAsync();
        var fileName = Path.GetFileName(path);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(fileName) <= 255);
        Assert.DoesNotContain(fileName, char.IsControl);
    }
}
