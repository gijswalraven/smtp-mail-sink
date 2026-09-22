using MailSink;

namespace MailSink.Tests;

/// <summary>
/// The rules around MailSink:Accounts. Folder names get the most attention here because they are
/// the one path component the sink does not generate itself.
/// </summary>
public class AccountOptionsTests
{
    private static MailSinkOptions WithAccounts(params (string Key, MailAccount Account)[] accounts)
    {
        var options = new MailSinkOptions();

        foreach (var (key, account) in accounts)
        {
            options.Accounts[key] = account;
        }

        return options;
    }

    private static MailAccount Account(string username = "app", string password = "s3cret", string? folder = null) =>
        new() { Username = username, Password = password, Folder = folder };

    [Fact]
    public void An_account_folder_defaults_to_its_key()
    {
        var options = WithAccounts(("orders", Account()));

        var resolved = Assert.Single(options.ResolveAccounts());

        Assert.Equal("orders", resolved.Key);
        Assert.Equal("orders", resolved.Folder);
    }

    [Fact]
    public void A_configured_folder_wins_over_the_key()
    {
        var options = WithAccounts(("crm", Account(folder: "crm-mail")));

        Assert.Equal("crm-mail", Assert.Single(options.ResolveAccounts()).Folder);
    }

    [Fact]
    public void An_empty_folder_means_the_root_of_the_mail_directory()
    {
        // How a deployment that predates accounts keeps the layout it already has.
        var options = WithAccounts(("legacy", Account(folder: string.Empty)));

        options.Validate(isDevelopment: true);

        Assert.Equal(string.Empty, Assert.Single(options.ResolveAccounts()).Folder);
    }

    [Fact]
    public void The_flat_pair_resolves_to_one_root_account()
    {
        var options = new MailSinkOptions { Username = "app", Password = "s3cret" };

        var resolved = Assert.Single(options.ResolveAccounts());

        Assert.Equal("app", resolved.Key);
        Assert.Equal("app", resolved.Username);
        Assert.Equal(string.Empty, resolved.Folder);
    }

    [Fact]
    public void No_credentials_at_all_resolves_to_no_accounts()
    {
        Assert.Empty(new MailSinkOptions().ResolveAccounts());
        Assert.False(new MailSinkOptions().HasCredentials);
    }

    [Fact]
    public void Accounts_make_AUTH_mandatory()
    {
        Assert.True(WithAccounts(("orders", Account())).HasCredentials);
    }

    [Fact]
    public void Validate_rejects_the_flat_pair_and_accounts_together()
    {
        var options = WithAccounts(("orders", Account()));
        options.Username = "app";
        options.Password = "s3cret";

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains("both configure credentials", ex.Message);
    }

    [Fact]
    public void Validate_rejects_an_account_without_a_username()
    {
        var options = WithAccounts(("orders", Account(username: string.Empty)));

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains("MailSink:Accounts:orders:Username is required", ex.Message);
    }

    [Fact]
    public void Validate_rejects_an_account_without_a_password()
    {
        var options = WithAccounts(("orders", Account(password: string.Empty)));

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains("MailSink:Accounts:orders:Password is required", ex.Message);
    }

    [Fact]
    public void Validate_rejects_two_accounts_sharing_a_username()
    {
        var options = WithAccounts(
            ("orders", Account(username: "app")),
            ("crm", Account(username: "app")));

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains("already used by another account", ex.Message);
    }

    [Fact]
    public void Validate_allows_two_accounts_to_share_a_folder_deliberately()
    {
        // An application and its test harness, say: separate credentials, one destination.
        var options = WithAccounts(
            ("app", Account(username: "app", folder: "shared")),
            ("harness", Account(username: "harness", folder: "shared")));

        options.Validate(isDevelopment: true);
    }

    [Fact]
    public void Validate_rejects_folders_that_differ_only_in_case()
    {
        var options = WithAccounts(
            ("orders", Account(username: "a", folder: "Shared")),
            ("crm", Account(username: "b", folder: "shared")));

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains("only in case", ex.Message);
    }

    [Fact]
    public void Validate_rejects_a_folder_that_is_not_a_usable_directory_name()
    {
        var options = WithAccounts(("orders", Account(folder: "../escape")));

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains("MailSink:Accounts:orders:Folder '../escape'", ex.Message);
        Assert.Contains("path separator", ex.Message);
    }

    [Fact]
    public void Validate_says_so_when_the_unusable_folder_came_from_the_key()
    {
        var options = WithAccounts(("orders/eu", Account()));

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains("The account key 'orders/eu'", ex.Message);
        Assert.Contains("Folder is not set", ex.Message);
    }

    [Fact]
    public void Validate_accepts_accounts_as_the_deployed_credential_requirement()
    {
        var options = WithAccounts(("orders", Account()));
        options.Tls.KeyVaultCertificateUri = "https://v.vault.azure.net/certificates/smtp";

        options.Validate(isDevelopment: false);
    }
}
