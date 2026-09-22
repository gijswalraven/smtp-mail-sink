using System.Net;
using System.Net.Mail;
using MimeKit;

namespace MailSink.Tests;

/// <summary>
/// The credential paths against a real listener. Configuring a pair makes AUTH mandatory, so a
/// sender that skips it or gets it wrong never gets as far as delivering.
/// </summary>
public class AuthenticationEndToEndTests
{
    private static Dictionary<string, string?> Credentials() => new()
    {
        ["MailSink:Username"] = "app",
        ["MailSink:Password"] = "s3cret",
    };

    private static MailMessage Message(string subject) =>
        new("app@example.test", "gijs@example.test", subject, "body");

    [Fact]
    public async Task The_configured_credentials_are_accepted()
    {
        await using var sink = await TestSink.StartAsync(Credentials());
        using var message = Message("Authenticated send");

        await sink.SendAsync(message, new NetworkCredential("app", "s3cret"));

        var parsed = await MimeMessage.LoadAsync(await sink.WaitForSingleFileAsync());
        Assert.Equal("Authenticated send", parsed.Subject);
    }

    [Theory]
    [InlineData("app", "wrong")]
    [InlineData("someone-else", "s3cret")]
    [InlineData("APP", "s3cret")] // Credentials are compared byte for byte, so case matters.
    public async Task Other_credentials_cannot_deliver(string user, string password)
    {
        await using var sink = await TestSink.StartAsync(Credentials());
        using var message = Message("Should not arrive");

        await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(
            () => sink.SendAsync(message, new NetworkCredential(user, password)));

        await sink.AssertNothingCapturedAsync();
    }

    [Fact]
    public async Task A_sender_that_does_not_authenticate_is_turned_away()
    {
        await using var sink = await TestSink.StartAsync(Credentials());
        using var message = Message("Should not arrive");

        await Assert.ThrowsAsync<MailKit.ServiceNotAuthenticatedException>(
            () => sink.SendAsync(message));

        await sink.AssertNothingCapturedAsync();
    }

    [Fact]
    public async Task Mail_is_accepted_without_AUTH_when_no_credentials_are_configured()
    {
        // The Development convenience: no credentials configured means no authenticator at all,
        // which is why AllowAnyCredentials no longer needs to exist.
        await using var sink = await TestSink.StartAsync();
        using var message = Message("Local development");

        await sink.SendAsync(message);

        var parsed = await MimeMessage.LoadAsync(await sink.WaitForSingleFileAsync());
        Assert.Equal("Local development", parsed.Subject);
    }

    [Fact]
    public async Task A_username_without_a_password_fails_to_start()
    {
        var settings = new Dictionary<string, string?> { ["MailSink:Username"] = "app" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestSink.StartAsync(settings));

        Assert.Contains("MailSink:Password is required", ex.Message);
    }

    private static Dictionary<string, string?> TwoAccounts() => new()
    {
        ["MailSink:Accounts:orders:Username"] = "orders-app",
        ["MailSink:Accounts:orders:Password"] = "s3cret",
        ["MailSink:Accounts:orders:Folder"] = "orders",
        ["MailSink:Accounts:crm:Username"] = "crm-app",
        ["MailSink:Accounts:crm:Password"] = "hunter2",
        ["MailSink:Accounts:crm:Folder"] = "crm-mail",
    };

    [Fact]
    public async Task Each_account_gets_its_own_folder()
    {
        await using var sink = await TestSink.StartAsync(TwoAccounts());
        using var fromOrders = Message("Order confirmed");
        using var fromCrm = Message("Lead assigned");

        await sink.SendAsync(fromOrders, new NetworkCredential("orders-app", "s3cret"));
        await sink.SendAsync(fromCrm, new NetworkCredential("crm-app", "hunter2"));

        var files = await sink.WaitForFilesAsync(2);
        var folders = files.Select(file => sink.RelativePathOf(file).Split('/')[0]).ToArray();

        Assert.Equal(["crm-mail", "orders"], folders.Order());
    }

    [Fact]
    public async Task An_account_folder_sits_above_the_date_folder()
    {
        await using var sink = await TestSink.StartAsync(TwoAccounts());
        using var message = Message("Order confirmed");

        await sink.SendAsync(message, new NetworkCredential("orders-app", "s3cret"));

        var relative = sink.RelativePathOf(await sink.WaitForSingleFileAsync());

        Assert.StartsWith($"orders/{DateTime.Now:yyyy-MM-dd}/", relative);
    }

    [Fact]
    public async Task One_accounts_username_with_anothers_password_delivers_nothing()
    {
        await using var sink = await TestSink.StartAsync(TwoAccounts());
        using var message = Message("Should not arrive");

        await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(
            () => sink.SendAsync(message, new NetworkCredential("orders-app", "hunter2")));

        await sink.AssertNothingCapturedAsync();
    }

    [Fact]
    public async Task An_account_without_a_password_fails_to_start()
    {
        var settings = new Dictionary<string, string?> { ["MailSink:Accounts:orders:Username"] = "app" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestSink.StartAsync(settings));

        Assert.Contains("MailSink:Accounts:orders:Password is required", ex.Message);
    }

    [Fact]
    public async Task A_folder_that_would_escape_the_mail_directory_fails_to_start()
    {
        var settings = new Dictionary<string, string?>
        {
            ["MailSink:Accounts:orders:Username"] = "app",
            ["MailSink:Accounts:orders:Password"] = "s3cret",
            ["MailSink:Accounts:orders:Folder"] = "../escape",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestSink.StartAsync(settings));

        Assert.Contains("path separator", ex.Message);
    }
}
