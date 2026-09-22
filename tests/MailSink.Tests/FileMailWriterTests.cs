using MailSink;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailSink.Tests;

public class FileMailWriterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mail-sink-tests", Guid.NewGuid().ToString("n"));

    private FileMailWriter Writer(Action<MailSinkOptions>? configure = null) =>
        new(
            TestOptions.For(settings =>
            {
                settings.MailDirectory = _root;
                configure?.Invoke(settings);
            }),
            new TestHostEnvironment(),
            NullLogger<FileMailWriter>.Instance);

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
        _ = Writer(o =>
        {
            o.Accounts["orders"] = new MailAccount { Username = "o", Password = "p" };
            o.Accounts["crm"] = new MailAccount { Username = "c", Password = "p", Folder = "crm-mail" };
        });

        Assert.True(Directory.Exists(Path.Combine(_root, "orders")));
        Assert.True(Directory.Exists(Path.Combine(_root, "crm-mail")));
    }

    // The sweep, against a real temp folder: deleting mail is the one thing the sink does that
    // cannot be undone, so what it keeps matters as much as what it removes.

    private static readonly DateTimeOffset Now = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The cutoff a 24-hour MaxAge produces at <see cref="Now"/>.</summary>
    private static readonly DateTimeOffset Cutoff = Now - TimeSpan.FromHours(24);

    /// <summary>Writes a file and backdates it, which is what the sweep judges age by.</summary>
    private string Existing(string relativePath, TimeSpan age)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Subject: hi\r\n\r\nbody");
        File.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
        return path;
    }

    [Fact]
    public async Task Sweep_deletes_messages_older_than_the_cutoff()
    {
        var stale = Existing("2026-09-19/101010-000_a.eml", TimeSpan.FromHours(48));
        var fresh = Existing("2026-09-21/090000-000_b.eml", TimeSpan.FromHours(1));

        var result = await Writer().SweepAsync(Cutoff, default);

        Assert.Equal(1, result.Messages);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task Sweep_keeps_a_message_exactly_at_the_cutoff()
    {
        // The boundary decides whether "keep 24 hours" means 24 or 23:59:59.999, and a file that
        // is exactly at the age has not yet passed it.
        var boundary = Existing("2026-09-20/100000-000_a.eml", TimeSpan.FromHours(24));

        var result = await Writer().SweepAsync(Cutoff, default);

        Assert.Equal(0, result.Messages);
        Assert.True(File.Exists(boundary));
    }

    [Fact]
    public async Task Sweep_leaves_files_that_are_not_eml()
    {
        var stale = Existing("2026-09-19/101010-000_a.eml", TimeSpan.FromHours(48));
        var other = Existing("2026-09-19/notes.txt", TimeSpan.FromHours(48));

        await Writer().SweepAsync(Cutoff, default);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task Sweep_removes_the_date_folders_it_empties_but_keeps_the_mail_directory()
    {
        Existing("2026-09-19/101010-000_a.eml", TimeSpan.FromHours(48));

        var result = await Writer().SweepAsync(Cutoff, default);

        Assert.Equal(new SweepResult(1, 1), result);
        Assert.False(Directory.Exists(Path.Combine(_root, "2026-09-19")));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task Sweep_keeps_a_date_folder_that_still_holds_mail()
    {
        Existing("2026-09-19/101010-000_a.eml", TimeSpan.FromHours(48));
        Existing("2026-09-19/101011-000_b.eml", TimeSpan.FromHours(1));

        var result = await Writer().SweepAsync(Cutoff, default);

        Assert.Equal(0, result.Folders);
        Assert.True(Directory.Exists(Path.Combine(_root, "2026-09-19")));
    }

    [Fact]
    public async Task Sweep_keeps_an_account_folder_it_has_emptied()
    {
        // The account folders are created at startup so an operator can see the layout; an empty
        // one says the account exists and has had no mail, which a missing one does not.
        Existing("orders/2026-09-19/101010-000_a.eml", TimeSpan.FromHours(48));

        var writer = Writer(settings => settings.Accounts["orders"] = new MailAccount
        {
            Username = "orders",
            Password = "pw",
        });

        var result = await writer.SweepAsync(Cutoff, default);

        Assert.Equal(new SweepResult(1, 1), result);
        Assert.False(Directory.Exists(Path.Combine(_root, "orders", "2026-09-19")));
        Assert.True(Directory.Exists(Path.Combine(_root, "orders")));
    }

    [Fact]
    public async Task Sweep_finds_nothing_to_do_in_an_empty_mail_directory()
    {
        var result = await Writer().SweepAsync(Cutoff, default);

        Assert.Equal(new SweepResult(0, 0), result);
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
