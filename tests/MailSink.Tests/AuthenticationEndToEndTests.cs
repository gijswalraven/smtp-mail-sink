using System.Net;
using System.Net.Mail;
using MimeKit;

namespace MailSink.Tests;

/// <summary>
/// The credential paths against a real listener: a configured pair is enforced, and
/// RequireAuthentication turns away a sender that never authenticates.
/// </summary>
public class AuthenticationEndToEndTests
{
    private static Dictionary<string, string?> Credentials(bool required = false) => new()
    {
        ["MailSink:Username"] = "app",
        ["MailSink:Password"] = "s3cret",
        ["MailSink:RequireAuthentication"] = required ? "true" : "false",
    };

    private static MailMessage Message(string subject) =>
        new("app@example.test", "gijs@example.test", subject, "body");

    [Fact]
    public async Task The_configured_credentials_are_accepted()
    {
        await using var sink = await TestSink.StartAsync(Credentials());
        using var message = Message("Authenticated send");

        sink.Send(message, new NetworkCredential("app", "s3cret"));

        var parsed = await MimeMessage.LoadAsync(await sink.WaitForSingleFileAsync());
        Assert.Equal("Authenticated send", parsed.Subject);
    }

    [Theory]
    [InlineData("app", "wrong")]
    [InlineData("someone-else", "s3cret")]
    [InlineData("APP", "s3cret")] // Credentials are compared byte for byte, so case matters.
    public async Task Other_credentials_cannot_deliver_once_authentication_is_required(
        string user, string password)
    {
        // AUTH answers 535 for a wrong pair either way -- see
        // FixedCredentialUserAuthenticatorTests -- but only RequireAuthentication stops the
        // session carrying on to deliver the message unauthenticated afterwards.
        await using var sink = await TestSink.StartAsync(Credentials(required: true));
        using var message = Message("Should not arrive");

        Assert.Throws<SmtpException>(() => sink.Send(message, new NetworkCredential(user, password)));

        await sink.AssertNothingCapturedAsync();
    }

    [Fact]
    public async Task Mail_is_still_accepted_without_AUTH_unless_it_is_required()
    {
        await using var sink = await TestSink.StartAsync(Credentials());
        using var message = Message("Unauthenticated but allowed");

        sink.Send(message);

        var parsed = await MimeMessage.LoadAsync(await sink.WaitForSingleFileAsync());
        Assert.Equal("Unauthenticated but allowed", parsed.Subject);
    }

    [Fact]
    public async Task RequireAuthentication_turns_away_a_sender_that_does_not_authenticate()
    {
        await using var sink = await TestSink.StartAsync(Credentials(required: true));
        using var message = Message("Should not arrive");

        Assert.Throws<SmtpException>(() => sink.Send(message));

        await sink.AssertNothingCapturedAsync();
    }

    [Fact]
    public async Task RequireAuthentication_still_lets_the_configured_credentials_through()
    {
        await using var sink = await TestSink.StartAsync(Credentials(required: true));
        using var message = Message("Authenticated and required");

        sink.Send(message, new NetworkCredential("app", "s3cret"));

        var parsed = await MimeMessage.LoadAsync(await sink.WaitForSingleFileAsync());
        Assert.Equal("Authenticated and required", parsed.Subject);
    }

    [Fact]
    public async Task A_username_without_a_password_fails_to_start()
    {
        var settings = new Dictionary<string, string?> { ["MailSink:Username"] = "app" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestSink.StartAsync(settings));

        Assert.Contains("MailSink:Password is required", ex.Message);
    }
}
