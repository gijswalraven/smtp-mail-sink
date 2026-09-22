using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Authentication;

namespace MailSink;

/// <summary>
/// Accepts exactly one username/password pair, the only authenticator the sink has. Registered
/// whenever MailSink:Username is configured, which is mandatory outside Development.
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
            // The password never reaches the log; the username and the client address do,
            // because a run of these is what a SIEM should be alerting on. SmtpServer drops the
            // session after MailSink:MaxAuthenticationAttempts of them.
            logger.LogWarning(
                "Rejected SMTP AUTH for {User} from {Client}: credentials do not match",
                user,
                SessionClient.Describe(context) ?? "an unknown address");
        }

        return Task.FromResult(accepted);
    }

    /// <summary>
    /// Compares in constant time. The length of the configured pair still leaks, which is not
    /// worth defending against here, but the comparison itself must not be what tells an attacker
    /// how much of a guess was right.
    /// </summary>
    private static bool Matches(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual));
}
