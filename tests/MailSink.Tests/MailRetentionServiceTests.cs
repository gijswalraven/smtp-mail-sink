using MailSink;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MailSink.Tests;

/// <summary>
/// The sweeper, against a real temp folder: deleting files is the one thing the sink does that
/// cannot be undone, so what it keeps matters as much as what it removes.
/// </summary>
public class MailRetentionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mail-sink-tests", Guid.NewGuid().ToString("n"));

    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private MailRetentionService Create(Action<MailSinkOptions>? configure = null)
    {
        var options = TestOptions.For(settings =>
        {
            settings.MailDirectory = _root;
            settings.Retention.MaxAge = TimeSpan.FromHours(24);
            configure?.Invoke(settings);
        });

        return new MailRetentionService(
            options,
            new FakeEnvironment(),
            _time,
            NullLogger<MailRetentionService>.Instance);
    }

    /// <summary>Writes a .eml file and backdates it, which is what the sweeper judges age by.</summary>
    private string Write(string relativePath, TimeSpan age)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Subject: hi\r\n\r\nbody");
        File.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
        return path;
    }

    [Fact]
    public void Sweep_deletes_files_older_than_the_configured_age()
    {
        var stale = Write("2026-09-19/101010-000_a.eml", TimeSpan.FromHours(48));
        var fresh = Write("2026-09-21/090000-000_b.eml", TimeSpan.FromHours(1));

        var (files, _) = Create().Sweep(_root);

        Assert.Equal(1, files);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Sweep_keeps_a_file_exactly_at_the_cutoff()
    {
        // The boundary decides whether "keep 24 hours" means 24 or 23:59:59.999, and a file that
        // is exactly at the age has not yet passed it.
        var boundary = Write("2026-09-20/100000-000_a.eml", TimeSpan.FromHours(24));

        var (files, _) = Create().Sweep(_root);

        Assert.Equal(0, files);
        Assert.True(File.Exists(boundary));
    }

    [Fact]
    public void Sweep_leaves_files_that_are_not_eml()
    {
        var stale = Write("2026-09-19/101010-000_a.eml", TimeSpan.FromHours(48));
        var other = Write("2026-09-19/notes.txt", TimeSpan.FromHours(48));

        Create().Sweep(_root);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Sweep_removes_the_date_folders_it_empties_but_keeps_the_mail_directory()
    {
        Write("2026-09-19/101010-000_a.eml", TimeSpan.FromHours(48));

        var (files, folders) = Create().Sweep(_root);

        Assert.Equal(1, files);
        Assert.Equal(1, folders);
        Assert.False(Directory.Exists(Path.Combine(_root, "2026-09-19")));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void Sweep_keeps_a_date_folder_that_still_holds_mail()
    {
        Write("2026-09-19/101010-000_a.eml", TimeSpan.FromHours(48));
        Write("2026-09-19/101011-000_b.eml", TimeSpan.FromHours(1));

        var (_, folders) = Create().Sweep(_root);

        Assert.Equal(0, folders);
        Assert.True(Directory.Exists(Path.Combine(_root, "2026-09-19")));
    }

    [Fact]
    public void Sweep_keeps_an_account_folder_it_has_emptied()
    {
        // The account folders are created at startup so an operator can see the layout; an empty
        // one says the account exists and has had no mail, which a missing one does not.
        Write("orders/2026-09-19/101010-000_a.eml", TimeSpan.FromHours(48));

        var (files, folders) = Create(settings =>
            settings.Accounts["orders"] = new MailAccount { Username = "orders", Password = "pw" })
            .Sweep(_root);

        Assert.Equal(1, files);
        Assert.Equal(1, folders);
        Assert.False(Directory.Exists(Path.Combine(_root, "orders", "2026-09-19")));
        Assert.True(Directory.Exists(Path.Combine(_root, "orders")));
    }

    [Fact]
    public void Sweep_does_nothing_when_the_mail_directory_is_not_there_yet()
    {
        var (files, folders) = Create().Sweep(_root);

        Assert.Equal((0, 0), (files, folders));
    }

    [Fact]
    public async Task The_service_sweeps_at_startup_and_again_each_interval()
    {
        var first = Write("2026-09-19/101010-000_a.eml", TimeSpan.FromHours(48));
        var second = Write("2026-09-21/093000-000_b.eml", TimeSpan.FromMinutes(30));

        var service = Create(settings => settings.Retention.SweepInterval = TimeSpan.FromHours(1));
        await service.StartAsync(CancellationToken.None);

        await WaitUntil(() => !File.Exists(first));
        Assert.True(File.Exists(second));

        // Far enough for the second file to have aged past 24 hours as well.
        _time.Advance(TimeSpan.FromHours(24));
        await WaitUntil(() => !File.Exists(second));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_service_deletes_nothing_when_no_age_is_configured()
    {
        var ancient = Write("2020-01-01/101010-000_a.eml", TimeSpan.FromDays(2000));

        var service = Create(settings => settings.Retention.MaxAge = TimeSpan.Zero);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.True(File.Exists(ancient));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(24, 0)]
    public void Validate_refuses_retention_that_cannot_do_what_it_says(int maxAgeHours, int intervalHours)
    {
        var options = new MailSinkOptions
        {
            Retention =
            {
                MaxAge = TimeSpan.FromHours(maxAgeHours),
                SweepInterval = TimeSpan.FromHours(intervalHours),
            },
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));
    }

    /// <summary>
    /// The sweeps run on the thread pool, so the assertions have to wait for one rather than
    /// assume it already happened.
    /// </summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException("The sweeper never got to it.");
    }

    /// <summary>Only the content root is read, and an absolute MailDirectory ignores even that.</summary>
    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "MailSink.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
