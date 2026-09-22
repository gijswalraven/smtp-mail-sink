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

    [Fact]
    public void ResolvePorts_falls_back_to_the_default_when_none_are_configured()
    {
        var ports = SmtpOptionsFactory.ResolvePorts(new MailSinkOptions { Ports = [] });

        Assert.Equal([MailSinkOptions.DefaultPort], ports);
    }

    [Fact]
    public void ResolvePorts_removes_duplicates()
    {
        // Regression: the configuration binder appends to array defaults rather than replacing
        // them, which once produced a listener bound to "1025, 1025".
        var ports = SmtpOptionsFactory.ResolvePorts(new MailSinkOptions { Ports = [1025, 1025, 2525] });

        Assert.Equal([1025, 2525], ports);
    }

    [Fact]
    public void ResolvePorts_keeps_the_configured_ports_only()
    {
        var ports = SmtpOptionsFactory.ResolvePorts(new MailSinkOptions { Ports = [25] });

        Assert.Equal([25], ports);
        Assert.DoesNotContain(MailSinkOptions.DefaultPort, ports);
    }

    [Fact]
    public void ResolveTlsPorts_falls_back_to_587_and_465()
    {
        var (startTls, implicitTls) = SmtpOptionsFactory.ResolveTlsPorts(new MailSinkOptions());

        Assert.Equal([MailSinkOptions.DefaultStartTlsPort], startTls);
        Assert.Equal([MailSinkOptions.DefaultImplicitTlsPort], implicitTls);
    }

    [Fact]
    public void ResolveTlsPorts_does_not_add_a_default_alongside_a_configured_port()
    {
        // Asking for implicit TLS alone must not also quietly open 587.
        var options = new MailSinkOptions { ImplicitTlsPorts = [4465] };

        var (startTls, implicitTls) = SmtpOptionsFactory.ResolveTlsPorts(options);

        Assert.Empty(startTls);
        Assert.Equal([4465], implicitTls);
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
            o.StartTlsPorts = [2525, 2526];
            o.MaxMessageSize = 1234;
            o.MaxAuthenticationAttempts = 2;
        });

        var built = SmtpOptionsFactory.Build(options, isDevelopment: false, new FakeCertificateFactory());

        Assert.Equal("test-sink", built.ServerName);
        Assert.Equal([2525, 2526], built.Endpoints.Select(e => e.Endpoint.Port));
        Assert.Equal(2, built.MaxAuthenticationAttempts);
    }

    [Fact]
    public void Build_opens_a_STARTTLS_and_an_implicit_TLS_endpoint_by_default()
    {
        var built = SmtpOptionsFactory.Build(Deployed(), isDevelopment: false, new FakeCertificateFactory());

        var startTls = Assert.Single(built.Endpoints, e => !e.IsSecure);
        var implicitTls = Assert.Single(built.Endpoints, e => e.IsSecure);

        Assert.Equal(MailSinkOptions.DefaultStartTlsPort, startTls.Endpoint.Port);
        Assert.Equal(MailSinkOptions.DefaultImplicitTlsPort, implicitTls.Endpoint.Port);
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
            new MailSinkOptions { Ports = [2525] }, isDevelopment: true, certificateFactory: null);

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
        var options = Deployed(o => o.Ports = [1025]);

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: false));

        Assert.Contains("only allowed in the Development environment", ex.Message);
    }
}
