using Azure.Core;
using Azure.Identity;

namespace MailSink;

/// <summary>
/// The credential the sink authenticates to Azure with, for the Key Vault certificate and for the
/// blob container it writes captured mail to.
/// </summary>
/// <remarks>
/// Deliberately not a plain <see cref="DefaultAzureCredential"/>: its chain ends in the developer
/// credentials, so a deployed container whose managed identity was not yet usable would quietly
/// try whoever last signed in to an az CLI inside the image -- an identity the deployment cannot
/// audit or revoke. Both callers only ever run outside Development, where a managed identity is
/// the only thing that should satisfy them, and failing loudly is the point.
/// </remarks>
internal static class SinkAzureCredential
{
    /// <param name="managedIdentityClientId">
    /// Client ID of the user-assigned managed identity to use. Needed because a container group
    /// can carry more than one identity and the credential cannot pick on its own. Empty leaves
    /// the choice to the chain, which is what a host with a single system-assigned identity wants.
    /// </param>
    public static TokenCredential Create(string managedIdentityClientId) =>
        new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = managedIdentityClientId.Length > 0
                ? managedIdentityClientId
                : null,
            ExcludeAzureCliCredential = true,
            ExcludeAzureDeveloperCliCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeInteractiveBrowserCredential = true,
            ExcludeVisualStudioCredential = true,
        });
}
