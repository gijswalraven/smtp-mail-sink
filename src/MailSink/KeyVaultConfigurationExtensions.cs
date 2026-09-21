using KeyVaultReferenceResolver;
using Microsoft.Extensions.Configuration;

namespace MailSink;

/// <summary>
/// Resolves <c>@Microsoft.KeyVault(...)</c> references in the application's configuration, so the
/// SMTP password can travel as a vault URI rather than as the secret itself.
/// </summary>
public static class KeyVaultConfigurationExtensions
{
    /// <summary>Both reference formats the resolver understands start with this.</summary>
    private const string ReferencePrefix = "@Microsoft.KeyVault(";

    /// <summary>
    /// Replaces every Key Vault reference with the secret it points at.
    /// <para>
    /// The resolver runs over a detached <see cref="ConfigurationBuilder"/> rather than over the
    /// host's configuration, for two reasons. The first is a bug in KeyVaultReferenceResolver
    /// 2.0.0, the version referenced here: it calls <c>builder.Build()</c> and disposes the
    /// result, and on a <see cref="ConfigurationManager"/> -- which is what
    /// <c>Host.CreateApplicationBuilder</c> hands you -- Build() returns the manager itself, so
    /// the application's configuration is disposed and the resolver's own next call throws
    /// ObjectDisposedException. That is fixed upstream; once a release carrying the fix is
    /// referenced, this could become a plain
    /// <c>builder.Configuration.AddKeyVaultReferenceResolver(...)</c>.
    /// </para>
    /// <para>
    /// The second reason outlives the bug: resolution is skipped entirely when no value is a
    /// reference, so a local run never builds an Azure credential chain.
    /// </para>
    /// </summary>
    public static void ResolveKeyVaultReferences(
        this ConfigurationManager configuration,
        bool allowDeveloperCredentials)
    {
        var current = configuration.AsEnumerable()
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value);

        var referenceKeys = ReferenceKeys(current);

        // Nothing to resolve is the normal local case. Returning early keeps a plain `dotnet run`
        // from building a credential chain and probing for a managed identity that is not there.
        if (referenceKeys.Length == 0)
        {
            return;
        }

        var options = configuration.GetSection(KeyVaultOptions.SectionName).Get<KeyVaultOptions>()
            ?? new KeyVaultOptions();

        var resolved = new ConfigurationBuilder()
            .AddInMemoryCollection(current)
            .AddKeyVaultReferenceResolver(resolver =>
            {
                // Off in Azure, so a container cannot silently fall back to a developer identity.
                resolver.AllowDeveloperCredentials = allowDeveloperCredentials;

                if (options.ManagedIdentityClientId.Length > 0)
                {
                    resolver.ManagedIdentityClientId = options.ManagedIdentityClientId;
                }

                foreach (var host in options.AllowedHosts)
                {
                    resolver.AllowedVaultHosts.Add(host);
                }
            })
            .Build();

        // Added last on purpose: the resolved secret has to outrank the reference it came from,
        // and that reference arrives as an environment variable, near the end of the chain.
        configuration.AddInMemoryCollection(
            referenceKeys.ToDictionary(key => key, key => resolved[key]));
    }

    /// <summary>Configuration keys whose value is a Key Vault reference rather than a literal.</summary>
    internal static string[] ReferenceKeys(IEnumerable<KeyValuePair<string, string?>> entries) =>
        [.. entries
            .Where(entry => entry.Value is not null
                && entry.Value.TrimStart().StartsWith(ReferencePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)];
}
