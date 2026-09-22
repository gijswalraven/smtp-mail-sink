using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Options;
using SmtpServer;

namespace MailSink;

/// <summary>
/// Serves the TLS certificate from a Key Vault certificate, re-reading it periodically so a
/// rotation takes effect without a restart.
/// </summary>
/// <remarks>
/// SmtpServer asks for the certificate once per session rather than once per process, which is
/// what makes refreshing possible at all -- hence <see cref="ICertificateFactory"/> instead of
/// handing the endpoint builder a fixed <see cref="X509Certificate"/>.
/// </remarks>
public sealed class KeyVaultCertificateFactory : ICertificateFactory, IDisposable
{
    private readonly TlsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KeyVaultCertificateFactory> _logger;
    private readonly SecretClient _client;
    private readonly string _certificateName;
    private readonly string? _certificateVersion;
    private readonly Lock _gate = new();

    private Cached? _cached;

    public KeyVaultCertificateFactory(
        IOptions<MailSinkOptions> options,
        IOptions<KeyVaultOptions> keyVaultOptions,
        TimeProvider timeProvider,
        ILogger<KeyVaultCertificateFactory> logger)
    {
        _options = options.Value.Tls;
        _timeProvider = timeProvider;
        _logger = logger;

        var (vaultUri, name, version) = ParseCertificateUri(_options.KeyVaultCertificateUri);
        _certificateName = name;
        _certificateVersion = version;

        var vault = keyVaultOptions.Value;
        RejectUnlistedHost(vaultUri, vault.AllowedHosts);

        // The vault holds the private key, so this is the one credential that must not fall back
        // to a developer identity: the factory is only ever registered outside Development.
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = vault.ManagedIdentityClientId.Length > 0
                ? vault.ManagedIdentityClientId
                : null,
            ExcludeAzureCliCredential = true,
            ExcludeAzureDeveloperCliCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeInteractiveBrowserCredential = true,
            ExcludeVisualStudioCredential = true,
        });

        _client = new SecretClient(vaultUri, credential);
    }

    public X509Certificate GetServerCertificate(ISessionContext sessionContext) => Current();

    /// <summary>
    /// The certificate to present, fetched on first use and re-fetched once
    /// <see cref="TlsOptions.RefreshInterval"/> has passed. Called at startup as well, so a
    /// vault that cannot be reached or a certificate without a private key fails the host
    /// immediately rather than on the first client connection.
    /// </summary>
    public X509Certificate2 Current()
    {
        var now = _timeProvider.GetUtcNow();

        var cached = _cached;
        if (cached is not null && now - cached.FetchedAt < _options.RefreshInterval)
        {
            return cached.Certificate;
        }

        lock (_gate)
        {
            // Another session may have refreshed it while we waited for the lock.
            cached = _cached;
            if (cached is not null && now - cached.FetchedAt < _options.RefreshInterval)
            {
                return cached.Certificate;
            }

            X509Certificate2 fetched;
            try
            {
                fetched = Fetch();
            }
            catch (Exception ex) when (cached is not null)
            {
                // A vault blip should not take the listener down while we hold a certificate that
                // still works. Keep serving it and try again on the next session.
                _logger.LogError(
                    ex,
                    "Could not refresh the TLS certificate from Key Vault; continuing with the cached one, which expires {NotAfter:u}",
                    cached.Certificate.NotAfter);

                _cached = cached with { FetchedAt = now };
                return cached.Certificate;
            }

            _logger.LogInformation(
                "Loaded TLS certificate {Subject} (thumbprint {Thumbprint}) valid until {NotAfter:u}",
                fetched.Subject,
                fetched.Thumbprint,
                fetched.NotAfter);

            _cached = new Cached(fetched, now);

            // Disposing releases the key material, and on Windows the on-disk key container that
            // came with it; the previous instance is no longer handed to new sessions.
            cached?.Certificate.Dispose();

            return fetched;
        }
    }

    /// <summary>
    /// Downloads the certificate <i>with</i> its private key. A Key Vault certificate object
    /// carries only the public half -- the full PKCS#12 lives in the secret of the same name and
    /// version, which is why this talks to <see cref="SecretClient"/> rather than to
    /// CertificateClient.
    /// </summary>
    private X509Certificate2 Fetch()
    {
        var secret = _client.GetSecret(_certificateName, _certificateVersion).Value;

        var certificate = secret.Properties.ContentType == "application/x-pem-file"
            ? X509Certificate2.CreateFromPem(secret.Value, secret.Value)
            : X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(secret.Value),
                password: null,
                keyStorageFlags: KeyStorageFlags);

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                $"Key Vault certificate '{_certificateName}' has no private key, so it cannot " +
                "terminate TLS. A certificate imported without its key, or one whose policy marks " +
                "it non-exportable, looks like this.");
        }

        return certificate;
    }

    /// <summary>
    /// Ephemeral keys never touch the disk, which is what we want -- but Windows cannot use one
    /// for server-side TLS (SChannel rejects the credential), so there it has to be a persisted
    /// key that we dispose of when the certificate is replaced.
    /// </summary>
    private static X509KeyStorageFlags KeyStorageFlags =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? X509KeyStorageFlags.Exportable
            : X509KeyStorageFlags.EphemeralKeySet;

    private static void RejectUnlistedHost(Uri vaultUri, string[] allowedHosts)
    {
        if (allowedHosts.Length == 0 ||
            allowedHosts.Contains(vaultUri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"MailSink:Tls:KeyVaultCertificateUri points at '{vaultUri.Host}', which is not in " +
            "MailSink:KeyVault:AllowedHosts. Add it there, or correct the URI.");
    }

    internal static (Uri VaultUri, string Name, string? Version) ParseCertificateUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"MailSink:Tls:KeyVaultCertificateUri '{value}' is not an absolute https URI.");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length is not (2 or 3) ||
            !segments[0].Equals("certificates", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"MailSink:Tls:KeyVaultCertificateUri '{value}' should look like " +
                "https://<vault>.vault.azure.net/certificates/<name>, optionally followed by a version.");
        }

        return (new Uri($"{uri.Scheme}://{uri.Authority}"), segments[1], segments.Length == 3 ? segments[2] : null);
    }

    public void Dispose() => _cached?.Certificate.Dispose();

    private sealed record Cached(X509Certificate2 Certificate, DateTimeOffset FetchedAt);
}
