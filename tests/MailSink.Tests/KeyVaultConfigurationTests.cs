using MailSink;

namespace MailSink.Tests;

public class KeyVaultConfigurationTests
{
    private static string[] Keys(params (string Key, string? Value)[] entries) =>
        KeyVaultConfigurationExtensions.ReferenceKeys(
            entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)));

    [Fact]
    public void A_SecretUri_reference_is_recognised()
    {
        var keys = Keys(("MailSink:Password",
            "@Microsoft.KeyVault(SecretUri=https://v.vault.azure.net/secrets/pw)"));

        Assert.Equal(["MailSink:Password"], keys);
    }

    [Fact]
    public void A_VaultName_reference_is_recognised()
    {
        var keys = Keys(("MailSink:Password",
            "@Microsoft.KeyVault(VaultName=v;SecretName=pw)"));

        Assert.Equal(["MailSink:Password"], keys);
    }

    [Theory]
    [InlineData("s3cret")]                                      // A literal password.
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not @Microsoft.KeyVault(SecretUri=...) here")] // Only a prefix counts.
    public void Anything_else_is_left_alone(string? value)
    {
        Assert.Empty(Keys(("MailSink:Password", value)));
    }

    [Fact]
    public void Leading_whitespace_and_casing_do_not_hide_a_reference()
    {
        var keys = Keys(("MailSink:Password", "  @microsoft.keyvault(SecretUri=https://v.vault.azure.net/secrets/pw)"));

        Assert.Equal(["MailSink:Password"], keys);
    }

    [Fact]
    public void Only_the_referencing_keys_are_returned()
    {
        var keys = Keys(
            ("MailSink:Username", "app"),
            ("MailSink:Password", "@Microsoft.KeyVault(SecretUri=https://v.vault.azure.net/secrets/pw)"),
            ("MailSink:ServerName", "mail-sink"));

        Assert.Equal(["MailSink:Password"], keys);
    }
}
