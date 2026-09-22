using MailSink;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailSink.Tests;

public class AccountUserAuthenticatorTests
{
    private static AccountUserAuthenticator Authenticator(Action<MailSinkOptions> configure) =>
        new(TestOptions.For(configure), NullLogger<AccountUserAuthenticator>.Instance);

    private static void WithAccounts(MailSinkOptions options)
    {
        options.Accounts["orders"] = new MailAccount { Username = "orders-app", Password = "s3cret" };
        options.Accounts["crm"] = new MailAccount { Username = "crm-app", Password = "hunter2", Folder = "crm-mail" };
    }

    private static Task<bool> AuthenticateAsync(string user, string password) =>
        Authenticator(WithAccounts).AuthenticateAsync(null!, user, password, CancellationToken.None);

    [Theory]
    [InlineData("orders-app", "s3cret")]
    [InlineData("crm-app", "hunter2")]
    public async Task Accepts_each_configured_account(string user, string password)
    {
        Assert.True(await AuthenticateAsync(user, password));
    }

    [Theory]
    [InlineData("orders-app", "hunter2")]   // One account's username with another's password.
    [InlineData("crm-app", "s3cret")]
    [InlineData("orders", "s3cret")]        // The key is not the username.
    [InlineData("ORDERS-APP", "s3cret")]    // Compared byte for byte, so case matters.
    [InlineData("orders-app", "S3CRET")]
    [InlineData("orders-app ", "s3cret")]   // No trimming: a stray space is a different credential.
    [InlineData("orders-app", "s3cretx")]   // A correct prefix is not enough.
    [InlineData("orders-app", "")]
    [InlineData("", "s3cret")]
    [InlineData("someone-else", "whatever")]
    public async Task Refuses_anything_else(string user, string password)
    {
        Assert.False(await AuthenticateAsync(user, password));
    }

    [Fact]
    public async Task The_flat_credential_pair_is_still_one_account()
    {
        var authenticator = Authenticator(o =>
        {
            o.Username = "app";
            o.Password = "s3cret";
        });

        Assert.True(await authenticator.AuthenticateAsync(null!, "app", "s3cret", CancellationToken.None));
        Assert.False(await authenticator.AuthenticateAsync(null!, "app", "wrong", CancellationToken.None));
    }

    [Fact]
    public async Task The_matched_account_is_left_on_the_session()
    {
        var context = new FakeSessionContext();

        var accepted = await Authenticator(WithAccounts)
            .AuthenticateAsync(context, "crm-app", "hunter2", CancellationToken.None);

        Assert.True(accepted);
        Assert.Equal("crm", SessionAccount.Current(context)?.Key);
        // The folder, not the key, is what the mail is filed under.
        Assert.Equal("crm-mail", SessionAccount.Folder(context));
    }

    [Fact]
    public async Task A_failed_attempt_leaves_no_account_on_the_session()
    {
        var context = new FakeSessionContext();

        await Authenticator(WithAccounts).AuthenticateAsync(context, "orders-app", "wrong", CancellationToken.None);

        Assert.Null(SessionAccount.Current(context));
        Assert.Equal(string.Empty, SessionAccount.Folder(context));
    }

    [Fact]
    public async Task A_failed_attempt_does_not_undo_one_that_succeeded()
    {
        // SmtpServer allows further AUTH commands on a session; a later wrong guess must not
        // move an already-authenticated session's mail to the root of the mail directory.
        var context = new FakeSessionContext();
        var authenticator = Authenticator(WithAccounts);

        await authenticator.AuthenticateAsync(context, "orders-app", "s3cret", CancellationToken.None);
        await authenticator.AuthenticateAsync(context, "orders-app", "wrong", CancellationToken.None);

        Assert.Equal("orders", SessionAccount.Folder(context));
    }
}
