using System.Security.Authentication;
using MailSink;

namespace MailSink.Tests;

public class SmtpOptionsFactoryTests
{
    /// <summary>The smallest options that satisfy the rules outside Development.</summary>
    private static MailSinkOptions Deployed(Action<MailSinkOptions>? configure = null)
    {
        var options = new MailSinkOptions
        {
            Username = "app",
            Password = "s3cret",
            Tls = { KeyVaultCertificateUri = "https://v.vault.azure.net/certificates/smtp" },
        };

        configure?.Invoke(options);
        return options;
    }

    [Theory]
    [InlineData(SmtpTlsMode.StartTls, MailSinkOptions.DefaultStartTlsPort)]
    [InlineData(SmtpTlsMode.Implicit, MailSinkOptions.DefaultImplicitTlsPort)]
    [InlineData(SmtpTlsMode.None, MailSinkOptions.DefaultPort)]
    public void ResolvePort_falls_back_to_the_conventional_port_for_the_mode(
        SmtpTlsMode mode, int expected)
    {
        var port = SmtpOptionsFactory.ResolvePort(new MailSinkOptions { TlsMode = mode }, isDevelopment: false);

        Assert.Equal(expected, port);
    }

    [Fact]
    public void A_development_run_without_a_certificate_listens_on_the_plain_text_port()
    {
        // Regression: TlsMode stays at its StartTls default when no certificate is configured,
        // and resolving the port from the mode alone put the sink on 587 while compose published
        // 1025, so nothing could reach it.
        var port = SmtpOptionsFactory.ResolvePort(new MailSinkOptions(), isDevelopment: true);

        Assert.Equal(MailSinkOptions.DefaultPort, port);
    }

    [Fact]
    public void A_configured_certificate_puts_it_back_on_the_submission_port()
    {
        var options = new MailSinkOptions
        {
            Tls = { KeyVaultCertificateUri = "https://v.vault.azure.net/certificates/smtp" },
        };

        Assert.Equal(MailSinkOptions.DefaultStartTlsPort, SmtpOptionsFactory.ResolvePort(options, isDevelopment: true));
    }

    [Theory]
    [InlineData(SmtpTlsMode.StartTls)]
    [InlineData(SmtpTlsMode.Implicit)]
    [InlineData(SmtpTlsMode.None)]
    public void ResolvePort_keeps_a_configured_port_whatever_the_mode(SmtpTlsMode mode)
    {
        var port = SmtpOptionsFactory.ResolvePort(new MailSinkOptions { Port = 2525, TlsMode = mode }, isDevelopment: false);

        Assert.Equal(2525, port);
    }

    [Fact]
    public void ResolveAddress_parses_the_configured_address()
    {
        var address = SmtpOptionsFactory.ResolveAddress(new MailSinkOptions { ListenAddress = "127.0.0.1" });

        Assert.Equal("127.0.0.1", address.ToString());
    }

    [Fact]
    public void ResolveAddress_rejects_a_hostname()
    {
        var options = new MailSinkOptions { ListenAddress = "localhost" };

        var ex = Assert.Throws<InvalidOperationException>(() => SmtpOptionsFactory.ResolveAddress(options));

        Assert.Contains("localhost", ex.Message);
    }

    [Fact]
    public void Build_maps_the_sink_options_onto_the_server_options()
    {
        var options = Deployed(o =>
        {
            o.ServerName = "test-sink";
            o.ListenAddress = "127.0.0.1";
            o.Port = 2525;
            o.MaxMessageSize = 1234;
            o.MaxAuthenticationAttempts = 2;
        });

        var built = SmtpOptionsFactory.Build(options, isDevelopment: false, new FakeCertificateFactory());

        Assert.Equal("test-sink", built.ServerName);
        Assert.Equal(2525, Assert.Single(built.Endpoints).Endpoint.Port);
        Assert.Equal(2, built.MaxAuthenticationAttempts);
    }

    [Fact]
    public void Build_opens_one_STARTTLS_endpoint_by_default()
    {
        var built = SmtpOptionsFactory.Build(Deployed(), isDevelopment: false, new FakeCertificateFactory());

        var endpoint = Assert.Single(built.Endpoints);
        Assert.False(endpoint.IsSecure);
        Assert.Equal(MailSinkOptions.DefaultStartTlsPort, endpoint.Endpoint.Port);
    }

    [Fact]
    public void Build_opens_one_implicit_TLS_endpoint_when_the_mode_says_so()
    {
        var options = Deployed(o => o.TlsMode = SmtpTlsMode.Implicit);

        var built = SmtpOptionsFactory.Build(options, isDevelopment: false, new FakeCertificateFactory());

        var endpoint = Assert.Single(built.Endpoints);
        Assert.True(endpoint.IsSecure);
        Assert.Equal(MailSinkOptions.DefaultImplicitTlsPort, endpoint.Endpoint.Port);
    }

    [Fact]
    public void Build_never_opens_a_second_endpoint_for_the_other_mode()
    {
        // The point of the single-port shape: asking for one mode does not quietly also listen
        // on the other, which is what the two port lists used to do.
        foreach (var mode in new[] { SmtpTlsMode.StartTls, SmtpTlsMode.Implicit })
        {
            var built = SmtpOptionsFactory.Build(
                Deployed(o => o.TlsMode = mode), isDevelopment: false, new FakeCertificateFactory());

            Assert.Single(built.Endpoints);
        }
    }

    [Fact]
    public void Build_requires_auth_and_refuses_it_unencrypted_on_every_deployed_endpoint()
    {
        var built = SmtpOptionsFactory.Build(Deployed(), isDevelopment: false, new FakeCertificateFactory());

        Assert.NotEmpty(built.Endpoints);
        Assert.All(built.Endpoints, e => Assert.True(e.AuthenticationRequired));
        Assert.All(built.Endpoints, e => Assert.False(e.AllowUnsecureAuthentication));
        Assert.All(built.Endpoints, e => Assert.NotNull(e.CertificateFactory));
    }

    [Theory]
    [InlineData(TlsProtocolFloor.Tls12, SslProtocols.Tls12 | SslProtocols.Tls13)]
    [InlineData(TlsProtocolFloor.Tls13, SslProtocols.Tls13)]
    public void Build_pins_the_protocol_floor(TlsProtocolFloor floor, SslProtocols expected)
    {
        var options = Deployed(o => o.Tls.MinimumProtocol = floor);

        var built = SmtpOptionsFactory.Build(options, isDevelopment: false, new FakeCertificateFactory());

        Assert.All(built.Endpoints, e => Assert.Equal(expected, e.SupportedSslProtocols));
    }

    [Fact]
    public void Build_listens_in_plain_text_in_development()
    {
        var built = SmtpOptionsFactory.Build(
            new MailSinkOptions { Port = 2525, TlsMode = SmtpTlsMode.None },
            isDevelopment: true,
            certificateFactory: null);

        var endpoint = Assert.Single(built.Endpoints);
        Assert.False(endpoint.IsSecure);
        Assert.Null(endpoint.CertificateFactory);

        // No credentials configured, so nothing to authenticate against and nothing to protect.
        Assert.False(endpoint.AuthenticationRequired);
        Assert.True(endpoint.AllowUnsecureAuthentication);
    }

    [Fact]
    public void Build_uses_TLS_in_development_too_once_a_certificate_is_configured()
    {
        var built = SmtpOptionsFactory.Build(Deployed(), isDevelopment: true, new FakeCertificateFactory());

        Assert.All(built.Endpoints, e => Assert.NotNull(e.CertificateFactory));
    }

    [Fact]
    public void Validate_rejects_a_username_without_a_password()
    {
        var options = new MailSinkOptions { Username = "app" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains("MailSink:Password is required", ex.Message);
    }

    [Fact]
    public void Validate_rejects_a_password_without_a_username()
    {
        var options = new MailSinkOptions { Password = "s3cret" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains("without MailSink:Username", ex.Message);
    }

    [Fact]
    public void Validate_accepts_a_bare_development_run()
    {
        // No credentials, no certificate: the local sink stays zero-configuration.
        new MailSinkOptions().Validate(isDevelopment: true);
    }

    [Fact]
    public void Validate_refuses_to_deploy_without_credentials()
    {
        var options = new MailSinkOptions
        {
            Tls = { KeyVaultCertificateUri = "https://v.vault.azure.net/certificates/smtp" },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: false));

        Assert.Contains("MailSink:Accounts, or the MailSink:Username and MailSink:Password pair, is", ex.Message);
    }

    [Fact]
    public void Validate_refuses_to_deploy_without_a_certificate()
    {
        var options = new MailSinkOptions { Username = "app", Password = "s3cret" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: false));

        Assert.Contains("MailSink:Tls:KeyVaultCertificateUri is required", ex.Message);
    }

    [Fact]
    public void Validate_refuses_a_plain_text_port_outside_development()
    {
        var options = Deployed(o => o.TlsMode = SmtpTlsMode.None);

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: false));

        Assert.Contains("only allowed in the Development environment", ex.Message);
    }
}
