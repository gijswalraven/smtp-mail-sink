using SmtpServer;
using SmtpServer.Authentication;

namespace MailSink;

/// <summary>
/// Accepts every credential. A sink has no accounts to protect; this exists only so that
/// applications hard-wired to authenticate before sending can connect without being changed.
/// </summary>
public sealed class AcceptAnyUserAuthenticator(ILogger<AcceptAnyUserAuthenticator> logger) : IUserAuthenticator
{
    public Task<bool> AuthenticateAsync(
        ISessionContext context,
        string user,
        string password,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("Accepting SMTP AUTH for {User} (credentials are not validated)", user);
        return Task.FromResult(true);
    }
}
