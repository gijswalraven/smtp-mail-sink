using SmtpServer;

namespace MailSink;

/// <summary>
/// Carries the account a session authenticated as from the authenticator to the message store,
/// through the session's property bag -- the same route SmtpServer itself uses for the remote
/// endpoint, and the one <see cref="SessionClient"/> reads.
/// </summary>
/// <remarks>
/// SmtpServer does record the username in <c>ISessionContext.Authentication</c>, but an account
/// is not its username: the folder is configured separately and the two need not match. Passing
/// the resolved account itself avoids looking the username back up on every message, and avoids
/// the question of what to do if that lookup ever finds nothing.
/// </remarks>
internal static class SessionAccount
{
    private const string Key = "MailSink.Account";

    /// <remarks>
    /// The context is nullable because a caller may have none -- SmtpServer hands the
    /// authenticator a real one, but a unit test exercising the comparison does not.
    /// </remarks>
    public static void Remember(ISessionContext? context, ResolvedAccount account)
    {
        if (context is not null)
        {
            context.Properties[Key] = account;
        }
    }

    /// <summary>The account this session authenticated as, or null when it did not.</summary>
    public static ResolvedAccount? Current(ISessionContext? context) =>
        context is not null &&
        context.Properties.TryGetValue(Key, out var value) &&
        value is ResolvedAccount account
            ? account
            : null;

    /// <summary>The folder the session's mail belongs in; empty for the mail directory itself.</summary>
    public static string Folder(ISessionContext? context) => Current(context)?.Folder ?? string.Empty;
}
