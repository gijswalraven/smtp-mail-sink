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
        };

        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public void ResolvePort_falls_back_to_the_default_when_none_is_configured()
    {
        Assert.Equal(MailSinkOptions.DefaultPort, SmtpOptionsFactory.ResolvePort(new MailSinkOptions()));
    }

    [Fact]
    public void ResolvePort_keeps_a_configured_port()
    {
        Assert.Equal(2525, SmtpOptionsFactory.ResolvePort(new MailSinkOptions { Port = 2525 }));
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

        Assert.Contains("is not a valid IP address", ex.Message);
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

        var built = SmtpOptionsFactory.Build(options, isDevelopment: false);

        Assert.Equal("test-sink", built.ServerName);
        Assert.Equal(2525, Assert.Single(built.Endpoints).Endpoint.Port);
        Assert.Equal(2, built.MaxAuthenticationAttempts);
    }

    [Fact]
    public void Build_opens_one_unencrypted_endpoint()
    {
        var options = Deployed(o => o.ListenAddress = "127.0.0.1");

        var built = SmtpOptionsFactory.Build(options, isDevelopment: false);

        var endpoint = Assert.Single(built.Endpoints);
        Assert.False(endpoint.IsSecure);
        Assert.Null(endpoint.CertificateFactory);
        Assert.Equal(MailSinkOptions.DefaultPort, endpoint.Endpoint.Port);
    }

    [Fact]
    public void Build_requires_auth_but_has_to_offer_it_unencrypted()
    {
        // There is no encrypted connection to wait for any more, so AUTH is offered as-is. This
        // test exists to make that trade explicit rather than incidental.
        var options = Deployed(o => o.ListenAddress = "127.0.0.1");

        var built = SmtpOptionsFactory.Build(options, isDevelopment: false);

        var endpoint = Assert.Single(built.Endpoints);
        Assert.True(endpoint.AuthenticationRequired);
        Assert.True(endpoint.AllowUnsecureAuthentication);
    }

    [Fact]
    public void Build_does_not_require_auth_when_no_credentials_are_configured()
    {
        var built = SmtpOptionsFactory.Build(new MailSinkOptions(), isDevelopment: true);

        var endpoint = Assert.Single(built.Endpoints);
        Assert.False(endpoint.AuthenticationRequired);
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
        new MailSinkOptions().Validate(isDevelopment: true);
    }

    [Fact]
    public void Validate_refuses_to_deploy_without_credentials()
    {
        var options = new MailSinkOptions { ListenAddress = "127.0.0.1" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: false));

        Assert.Contains("required outside the Development environment", ex.Message);
    }

    [Fact]
    public void Validate_refuses_a_port_that_is_not_one()
    {
        var options = Deployed(o =>
        {
            o.ListenAddress = "127.0.0.1";
            o.Port = 70000;
        });

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: false));

        Assert.Contains("not a usable port", ex.Message);
    }
}
