namespace MailSink;

/// <summary>
/// Settings for resolving <c>@Microsoft.KeyVault(SecretUri=...)</c> references, bound from the
/// "MailSink:KeyVault" configuration section. Set by deploy.ps1; empty for a local run.
/// </summary>
public sealed class KeyVaultOptions
{
    public const string SectionName = "MailSink:KeyVault";

    /// <summary>
    /// Vault hosts the resolver may contact, e.g. <c>mailsink1a2b3c4d.vault.azure.net</c>. Empty
    /// allows any Key Vault host; naming yours means a reference that points somewhere else --
    /// an attacker's vault, or another team's -- is refused rather than fetched.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>
    /// Client ID of the user-assigned managed identity to authenticate with. Needed because a
    /// container group can carry more than one identity, and the default credential cannot pick
    /// on its own. Empty falls back to the default credential chain.
    /// </summary>
    public string ManagedIdentityClientId { get; set; } = string.Empty;
}
