namespace MailSink;

/// <summary>
/// Where captured mail goes when it goes to Azure Blob Storage, bound from the "MailSink:Blob"
/// configuration section. Set by deploy.ps1; empty everywhere else, and the sink then writes
/// .eml files to <see cref="MailSinkOptions.MailDirectory"/> instead.
/// </summary>
public sealed class BlobOptions
{
    /// <summary>
    /// Blob endpoint of the storage account, e.g.
    /// <c>https://mailsink1a2b3c4d.blob.core.windows.net</c>. Empty writes to the filesystem.
    /// </summary>
    /// <remarks>
    /// The account rather than one container, because each account's mail goes to a container of
    /// its own -- <see cref="MailAccount.Folder"/> names it, the same value that names the folder
    /// on a filesystem. A container is what an Entra ID role can be scoped to, so that is what
    /// makes "this team reads its own mail and no one else's" something the storage account
    /// enforces rather than something the sink merely arranges.
    /// </remarks>
    public string ServiceUri { get; set; } = string.Empty;

    /// <summary>
    /// Container for mail that belongs to no account: the single-client
    /// <see cref="MailSinkOptions.Username"/> shorthand, an account with an explicitly empty
    /// folder, and the unauthenticated Development case.
    /// </summary>
    public string Container { get; set; } = "mail";

    /// <summary>
    /// Client ID of the user-assigned managed identity to authenticate with. Needed because a
    /// container group can carry more than one identity, and the credential cannot pick on its
    /// own. Empty leaves the choice to the credential chain.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="KeyVaultOptions.ManagedIdentityClientId"/>, and normally the same
    /// value. Each section says which identity reaches that resource, so a deployment can move one
    /// of the two to its own identity without the other silently coming along.
    /// </remarks>
    public string ManagedIdentityClientId { get; set; } = string.Empty;

    /// <summary>True once an account is configured, and so when mail goes to blob storage.</summary>
    public bool IsConfigured => ServiceUri.Length > 0;
}
