using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Authentication;

namespace MailSink;

/// <summary>
/// Accepts any of the configured accounts, the only authenticator the sink has. Registered
/// whenever at least one account is configured, which is mandatory outside Development. The
/// account that matched is remembered on the session, so the capture can file its mail under
/// that account's folder.
/// </summary>
public sealed class AccountUserAuthenticator(
    IOptions<MailSinkOptions> options,
    ILogger<AccountUserAuthenticator> logger) : IUserAuthenticator
{
    private readonly IReadOnlyList<ResolvedAccount> _accounts = options.Value.ResolveAccounts();

    public Task<bool> AuthenticateAsync(
        ISessionContext context,
        string user,
        string password,
        CancellationToken cancellationToken)
    {
        ResolvedAccount? matched = null;

        // Every account is compared on every attempt, with no early exit. Stopping at the first
        // match would make a session that authenticates as the first account measurably faster
        // than one authenticating as the last, and a failed attempt slower than both -- which is
        // enough to enumerate which usernames exist.
        foreach (var account in _accounts)
        {
            // Non-short-circuiting '&' for the same reason it is used within a pair: '&&' would
            // skip the password comparison whenever the username did not match, and the
            // difference in response time would say which half failed.
            if (Matches(account.Username, user) & Matches(account.Password, password))
            {
                matched = account;
            }
        }

        if (matched is { } accepted)
        {
            SessionAccount.Remember(context, accepted);
            logger.LogDebug("Accepted SMTP AUTH for {User} as account {Account}", user, accepted.Key);
            return Task.FromResult(true);
        }

        // The password never reaches the log; the username and the client address do, because a
        // run of these is what a SIEM should be alerting on. SmtpServer drops the session after
        // MailSink:MaxAuthenticationAttempts of them.
        logger.LogWarning(
            "Rejected SMTP AUTH for {User} from {Client}: credentials do not match any account",
            user,
            SessionClient.Describe(context) ?? "an unknown address");

        return Task.FromResult(false);
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
