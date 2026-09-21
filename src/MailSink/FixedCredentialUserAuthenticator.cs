using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Authentication;

namespace MailSink;

/// <summary>
/// Accepts exactly one username/password pair. Registered instead of
/// <see cref="AcceptAnyUserAuthenticator"/> when MailSink:Username is configured, so a test can
/// assert that an application sends the credentials it was told to send.
/// </summary>
public sealed class FixedCredentialUserAuthenticator(
    IOptions<MailSinkOptions> options,
    ILogger<FixedCredentialUserAuthenticator> logger) : IUserAuthenticator
{
    private readonly MailSinkOptions _options = options.Value;

    public Task<bool> AuthenticateAsync(
        ISessionContext context,
        string user,
        string password,
        CancellationToken cancellationToken)
    {
        // Non-short-circuiting '&' on purpose: '&&' would skip the password comparison whenever the
        // username did not match, and the difference in response time would say which half failed.
        // That would give away most of what FixedTimeEquals is here to protect.
        var accepted = Matches(_options.Username, user) & Matches(_options.Password, password);

        if (accepted)
        {
            logger.LogDebug("Accepted SMTP AUTH for {User}", user);
        }
        else
        {
            // The password never reaches the log; the username does, because knowing which
            // account an application tried is the whole point of turning this on.
            logger.LogWarning("Rejected SMTP AUTH for {User}: credentials do not match", user);
        }

        return Task.FromResult(accepted);
    }

    /// <summary>
    /// Compares in constant time. Length still leaks, and a sink is not a security boundary, but
    /// the credentials are an application's real SMTP credentials often enough to be worth it.
    /// </summary>
    private static bool Matches(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual));
}
