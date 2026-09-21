using MailSink;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailSink.Tests;

public class FixedCredentialUserAuthenticatorTests
{
    private static Task<bool> AuthenticateAsync(string user, string password)
    {
        var options = TestOptions.For(o =>
        {
            o.Username = "app";
            o.Password = "s3cret";
        });

        var authenticator = new FixedCredentialUserAuthenticator(
            options, NullLogger<FixedCredentialUserAuthenticator>.Instance);

        return authenticator.AuthenticateAsync(null!, user, password, CancellationToken.None);
    }

    [Fact]
    public async Task Accepts_the_configured_pair()
    {
        Assert.True(await AuthenticateAsync("app", "s3cret"));
    }

    [Theory]
    [InlineData("app", "wrong")]
    [InlineData("someone-else", "s3cret")]
    [InlineData("APP", "s3cret")]      // Compared byte for byte, so case matters.
    [InlineData("app", "S3CRET")]
    [InlineData("app", "")]
    [InlineData("", "s3cret")]
    [InlineData("app ", "s3cret")]     // No trimming: a stray space is a different credential.
    [InlineData("app", "s3cretx")]     // A correct prefix is not enough.
    public async Task Refuses_anything_else(string user, string password)
    {
        Assert.False(await AuthenticateAsync(user, password));
    }
}
