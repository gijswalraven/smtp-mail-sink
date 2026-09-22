using MailSink;

namespace MailSink.Tests;

/// <summary>
/// The Development convenience -- no TLS, often no credentials -- is the one configuration that
/// must not end up reachable from the network by inheriting a default. These pin the rule that
/// keeps it on loopback unless someone says otherwise in as many words.
/// </summary>
public class PlainTextReachTests
{
    private static MailSinkOptions PlainText(Action<MailSinkOptions>? configure = null)
    {
        var options = new MailSinkOptions { TlsMode = SmtpTlsMode.None };
        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public void The_default_bind_is_loopback()
    {
        Assert.Equal("127.0.0.1", new MailSinkOptions().ListenAddress);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void An_unencrypted_listener_may_bind_loopback(string address)
    {
        PlainText(o => o.ListenAddress = address).Validate(isDevelopment: true);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.30.1.4")]
    [InlineData("::")]
    public void An_unencrypted_listener_may_not_bind_anything_else(string address)
    {
        var options = PlainText(o => o.ListenAddress = address);

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains("AllowPlainTextFromAnyAddress", ex.Message);
    }

    [Fact]
    public void The_opt_in_is_what_lets_it_reach_the_network()
    {
        // What the compose file does: inside a container 0.0.0.0 is the only reachable address,
        // and the published port is bound to the host loopback instead.
        PlainText(o =>
        {
            o.ListenAddress = "0.0.0.0";
            o.AllowPlainTextFromAnyAddress = true;
        }).Validate(isDevelopment: true);
    }

    [Fact]
    public void A_TLS_listener_may_bind_anything_without_the_opt_in()
    {
        // The guard is about plain text, not about the address: a deployed container group binds
        // 0.0.0.0 and is none of this rule's business.
        var options = new MailSinkOptions
        {
            Username = "app",
            Password = "s3cret",
            ListenAddress = "0.0.0.0",
            Tls = { KeyVaultCertificateUri = "https://v.vault.azure.net/certificates/smtp" },
        };

        options.Validate(isDevelopment: false);
        options.Validate(isDevelopment: true);
    }

    [Fact]
    public void A_development_run_without_a_certificate_is_plain_text_whatever_the_mode_says()
    {
        // TlsMode defaults to StartTls, but with no certificate there is nothing to upgrade to,
        // so the guard still applies -- otherwise the default mode would be a way around it.
        var options = new MailSinkOptions { ListenAddress = "0.0.0.0" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains("AllowPlainTextFromAnyAddress", ex.Message);
    }

    [Fact]
    public void The_message_size_ceiling_fits_a_modest_host()
    {
        // 10 MB across the default 64 concurrent sessions is 640 MB held in memory at worst,
        // which fits the 1 GB container group the deploy script asks for.
        var options = new MailSinkOptions();

        Assert.Equal(10 * 1024 * 1024, options.MaxMessageSize);
        Assert.True((long)options.MaxMessageSize * options.MaxConcurrentSessions < 1024L * 1024 * 1024);
    }
}
