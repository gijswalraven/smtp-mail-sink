using MailSink;

namespace MailSink.Tests;

/// <summary>
/// Nothing this sink serves is encrypted, so binding an address other machines can reach is the
/// configuration that must never be arrived at by inheriting a default. These pin the rule that
/// keeps it on loopback unless someone says otherwise in as many words.
/// </summary>
public class PlainTextReachTests
{
    private static MailSinkOptions PlainText(Action<MailSinkOptions>? configure = null)
    {
        var options = new MailSinkOptions();
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
    public void The_waiver_lets_a_listener_bind_any_address()
    {
        // A deployed container group has to bind 0.0.0.0 to be reachable at all, so it says so.
        var options = new MailSinkOptions
        {
            Username = "app",
            Password = "s3cret",
            ListenAddress = "0.0.0.0",
            AllowPlainTextFromAnyAddress = true,
        };

        options.Validate(isDevelopment: false);
        options.Validate(isDevelopment: true);
    }

    [Fact]
    public void A_listener_on_any_address_is_refused_without_the_waiver()
    {
        var options = new MailSinkOptions { ListenAddress = "0.0.0.0" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains("AllowPlainTextFromAnyAddress", ex.Message);
    }
}
