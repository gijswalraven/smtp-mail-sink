using MailSink;
using Microsoft.Extensions.Hosting;

namespace MailSink.Tests;

public class FileMailWriterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mail-sink-tests", Guid.NewGuid().ToString("n"));

    private FileMailWriter Writer() =>
        new(TestOptions.For(o => o.MailDirectory = _root), new TestHostEnvironment());

    [Fact]
    public async Task Writes_the_message_under_the_mail_directory()
    {
        var path = await Writer().WriteAsync(new MailName("2026-09-21", "m.eml"), [1, 2, 3], default);

        Assert.StartsWith(_root, path);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Gives_a_second_message_with_the_same_name_a_suffix()
    {
        var writer = Writer();
        var name = new MailName(string.Empty, "m.eml");

        var first = await writer.WriteAsync(name, [1], default);
        var second = await writer.WriteAsync(name, [2], default);

        Assert.NotEqual(first, second);
        Assert.EndsWith("m_2.eml", second);
    }

    [Fact]
    public async Task Refuses_a_folder_that_escapes_the_mail_directory()
    {
        // MailNaming cannot produce this today -- the point of the check is that it still holds
        // if that sanitising is ever weakened or another IMailWriter caller appears.
        var name = new MailName(Path.Combine("..", "escape"), "m.eml");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Writer().WriteAsync(name, [1], default));

        Assert.Contains("outside the mail directory", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "..", "escape")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "MailSink.Tests";

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
