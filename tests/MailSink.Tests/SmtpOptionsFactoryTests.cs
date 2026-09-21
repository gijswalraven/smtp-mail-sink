using MailSink;

namespace MailSink.Tests;

public class SmtpOptionsFactoryTests
{
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
        var options = new MailSinkOptions
        {
            ServerName = "test-sink",
            ListenAddress = "127.0.0.1",
            Ports = [2525, 2526],
            MaxMessageSize = 1234,
        };

        var built = SmtpOptionsFactory.Build(options);

        Assert.Equal("test-sink", built.ServerName);
        Assert.Equal([2525, 2526], built.Endpoints.Select(e => e.Endpoint.Port));
        Assert.All(built.Endpoints, e => Assert.False(e.IsSecure));
        Assert.All(built.Endpoints, e => Assert.False(e.AuthenticationRequired));
    }

    [Fact]
    public void Build_offers_unsecure_auth_only_when_credentials_are_accepted()
    {
        var accepting = SmtpOptionsFactory.Build(new MailSinkOptions { AllowAnyCredentials = true });
        var refusing = SmtpOptionsFactory.Build(new MailSinkOptions { AllowAnyCredentials = false });

        Assert.All(accepting.Endpoints, e => Assert.True(e.AllowUnsecureAuthentication));
        Assert.All(refusing.Endpoints, e => Assert.False(e.AllowUnsecureAuthentication));
    }

    [Fact]
    public void Build_offers_auth_for_a_fixed_credential_pair_even_with_AllowAnyCredentials_off()
    {
        var options = new MailSinkOptions
        {
            AllowAnyCredentials = false,
            Username = "app",
            Password = "s3cret",
        };

        var built = SmtpOptionsFactory.Build(options);

        Assert.All(built.Endpoints, e => Assert.True(e.AllowUnsecureAuthentication));
    }

    [Fact]
    public void Build_marks_the_endpoints_as_requiring_auth_when_configured()
    {
        var options = new MailSinkOptions
        {
            Username = "app",
            Password = "s3cret",
            RequireAuthentication = true,
        };

        var built = SmtpOptionsFactory.Build(options);

        Assert.All(built.Endpoints, e => Assert.True(e.AuthenticationRequired));
    }

    [Fact]
    public void ValidateCredentials_rejects_a_username_without_a_password()
    {
        var options = new MailSinkOptions { Username = "app" };

        var ex = Assert.Throws<InvalidOperationException>(options.ValidateCredentials);

        Assert.Contains("MailSink:Password is required", ex.Message);
    }

    [Fact]
    public void ValidateCredentials_rejects_a_password_without_a_username()
    {
        var options = new MailSinkOptions { Password = "s3cret" };

        var ex = Assert.Throws<InvalidOperationException>(options.ValidateCredentials);

        Assert.Contains("without MailSink:Username", ex.Message);
    }

    [Fact]
    public void ValidateCredentials_rejects_requiring_auth_that_nothing_can_satisfy()
    {
        var options = new MailSinkOptions { AllowAnyCredentials = false, RequireAuthentication = true };

        var ex = Assert.Throws<InvalidOperationException>(options.ValidateCredentials);

        Assert.Contains("nothing can authenticate", ex.Message);
    }

    [Fact]
    public void ValidateCredentials_accepts_the_defaults()
    {
        new MailSinkOptions().ValidateCredentials();
    }
}
