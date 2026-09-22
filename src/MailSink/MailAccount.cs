namespace MailSink;

/// <summary>
/// One credential pair, and the folder the mail it delivers is written to. Configured under
/// "MailSink:Accounts:&lt;key&gt;", where the key names the account in logs and in the deploy
/// script's vault secrets but never reaches the filesystem -- <see cref="Folder"/> does.
/// </summary>
public sealed class MailAccount
{
    /// <summary>Username this account must present at AUTH. Compared byte for byte.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password that goes with <see cref="Username"/>. Required.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Subfolder of the mail directory this account's mail lands in. One segment, no separators.
    /// </summary>
    /// <remarks>
    /// Null means "not configured", and the account key is used instead, which is what makes
    /// the property optional in the common case where the two are the same. An explicitly empty
    /// string is a different thing: it writes to the root of the mail directory, which is how a
    /// deployment that predates accounts keeps the layout it already has.
    /// </remarks>
    public string? Folder { get; set; }

    /// <summary><see cref="Folder"/>, or the account key when it was left out.</summary>
    public string FolderOrKey(string key) => Folder ?? key;
}

/// <summary>
/// An account with everything resolved: no null folder, no distinction left between a flat
/// MailSink:Username pair and an entry under MailSink:Accounts. What the authenticator and the
/// writer work with.
/// </summary>
/// <param name="Key">Name of the account, for logs. The username for a flat pair.</param>
/// <param name="Folder">Effective folder, empty for the root of the mail directory.</param>
public readonly record struct ResolvedAccount(string Key, string Username, string Password, string Folder);
