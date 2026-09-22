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
        var path = await Writer().WriteAsync(new MailName(string.Empty, "2026-09-21", "m.eml"), [1, 2, 3], default);

        Assert.StartsWith(_root, path);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Gives_a_second_message_with_the_same_name_a_suffix()
    {
        var writer = Writer();
        var name = new MailName(string.Empty, string.Empty, "m.eml");

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
        var name = new MailName(string.Empty, Path.Combine("..", "escape"), "m.eml");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Writer().WriteAsync(name, [1], default));

        Assert.Contains("outside the mail directory", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "..", "escape")));
    }

    [Fact]
    public async Task Writes_into_the_account_folder_above_the_date_folder()
    {
        var path = await Writer().WriteAsync(new MailName("orders", "2026-09-21", "m.eml"), [1], default);

        Assert.Equal(Path.Combine(_root, "orders", "2026-09-21", "m.eml"), path);
    }

    [Fact]
    public async Task Keeps_two_accounts_apart()
    {
        var writer = Writer();
        var name = new MailName("orders", "2026-09-21", "m.eml");

        var orders = await writer.WriteAsync(name, [1], default);
        var crm = await writer.WriteAsync(name with { Account = "crm" }, [2], default);

        // Same file name, no suffix on either: the folders are what separate them.
        Assert.EndsWith(Path.Combine("orders", "2026-09-21", "m.eml"), orders);
        Assert.EndsWith(Path.Combine("crm", "2026-09-21", "m.eml"), crm);
    }

    [Fact]
    public async Task Refuses_an_account_folder_that_escapes_the_mail_directory()
    {
        // MailSinkOptions.Validate rejects this at startup; the check here is what holds if a
        // folder ever reaches the writer without going through that validation.
        var name = new MailName(Path.Combine("..", "escape"), "2026-09-21", "m.eml");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Writer().WriteAsync(name, [1], default));

        Assert.Contains("outside the mail directory", ex.Message);
    }

    [Fact]
    public void Creates_each_account_folder_up_front()
    {
        _ = new FileMailWriter(
            TestOptions.For(o =>
            {
                o.MailDirectory = _root;
                o.Accounts["orders"] = new MailAccount { Username = "o", Password = "p" };
                o.Accounts["crm"] = new MailAccount { Username = "c", Password = "p", Folder = "crm-mail" };
            }),
            new TestHostEnvironment());

        Assert.True(Directory.Exists(Path.Combine(_root, "orders")));
        Assert.True(Directory.Exists(Path.Combine(_root, "crm-mail")));
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
