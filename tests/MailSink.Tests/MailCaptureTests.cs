using System.Text;
using MailSink;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MailSink.Tests;

public class MailCaptureTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 21, 10, 23, 57, 243, TimeSpan.Zero);

    private static MailCapture Create(
        IMailWriter writer,
        IMessageMetadataReader? metadata = null,
        Action<MailSinkOptions>? configure = null,
        DateTimeOffset? now = null)
    {
        var time = new FakeTimeProvider(now ?? At);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);

        return new MailCapture(
            TestOptions.For(configure),
            writer,
            metadata ?? new StubMetadataReader("Order confirmed"),
            time,
            NullLogger<MailCapture>.Instance);
    }

    private static IncomingMessage Message(string body = "hello") =>
        new(Encoding.UTF8.GetBytes(body), "app@example.test", ["gijs@example.test"]);

    [Fact]
    public async Task Capture_writes_the_raw_bytes_unchanged()
    {
        var writer = new InMemoryMailWriter();
        var raw = Encoding.UTF8.GetBytes("Subject: hi\r\n\r\nbody");

        var result = await Create(writer).CaptureAsync(
            new IncomingMessage(raw, "app@example.test", ["gijs@example.test"]), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(raw, Assert.Single(writer.Written).Raw);
    }

    [Fact]
    public async Task Capture_names_the_file_from_the_frozen_clock()
    {
        var writer = new InMemoryMailWriter();

        await Create(writer).CaptureAsync(Message(), CancellationToken.None);

        Assert.Equal(
            "2026-09-21/102357-243_gijs@example.test_Order-confirmed.eml",
            Assert.Single(writer.Written).Name.RelativePath);
    }

    [Fact]
    public async Task Capture_returns_the_location_from_the_writer()
    {
        var writer = new InMemoryMailWriter();

        var result = await Create(writer).CaptureAsync(Message(), CancellationToken.None);

        Assert.Equal("in-memory/2026-09-21/102357-243_gijs@example.test_Order-confirmed.eml", result.Location);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Capture_honours_GroupByDate()
    {
        var writer = new InMemoryMailWriter();

        await Create(writer, configure: o => o.GroupByDate = false)
            .CaptureAsync(Message(), CancellationToken.None);

        Assert.Equal(string.Empty, Assert.Single(writer.Written).Name.Date);
    }

    [Fact]
    public async Task Capture_still_stores_a_message_whose_headers_cannot_be_read()
    {
        var writer = new InMemoryMailWriter();
        var capture = Create(writer, new StubMetadataReader(null, parsed: false));

        var result = await capture.CaptureAsync(Message("not a mime message at all"), CancellationToken.None);

        Assert.True(result.Succeeded);
        // No subject in the name, but the bytes are kept.
        Assert.Equal("2026-09-21/102357-243_gijs@example.test.eml", Assert.Single(writer.Written).Name.RelativePath);
    }

    [Fact]
    public async Task Capture_reports_failure_instead_of_throwing_when_the_writer_fails()
    {
        var writer = new InMemoryMailWriter { ThrowOnWrite = new IOException("disk full") };

        var result = await Create(writer).CaptureAsync(Message(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("disk full", result.Error);
        Assert.Null(result.Location);
    }

    [Fact]
    public async Task Capture_files_a_message_under_the_account_that_delivered_it()
    {
        var writer = new InMemoryMailWriter();
        var message = new IncomingMessage(
            [1], "app@example.test", ["gijs@example.test"], AccountFolder: "orders");

        await Create(writer).CaptureAsync(message, CancellationToken.None);

        Assert.Equal(
            "orders/2026-09-21/102357-243_gijs@example.test_Order-confirmed.eml",
            Assert.Single(writer.Written).Name.RelativePath);
    }

    [Fact]
    public async Task Capture_writes_to_the_root_when_the_session_had_no_account()
    {
        // Only reachable in Development, where the sink may run with no accounts at all.
        var writer = new InMemoryMailWriter();

        await Create(writer).CaptureAsync(Message(), CancellationToken.None);

        Assert.Equal(string.Empty, Assert.Single(writer.Written).Name.Account);
    }
}
