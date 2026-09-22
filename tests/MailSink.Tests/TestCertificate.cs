using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SmtpServer;

namespace MailSink.Tests;

/// <summary>
/// A self-signed certificate for the loopback interface, standing in for the Key Vault
/// certificate that <see cref="MailSink.KeyVaultCertificateFactory"/> would fetch. Generated once
/// per test run, because a 2048-bit key costs more than every assertion that uses it.
/// </summary>
internal static class TestCertificate
{
    private static readonly Lazy<X509Certificate2> Lazy = new(Create);

    public static X509Certificate2 Shared => Lazy.Value;

    private static X509Certificate2 Create()
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddDnsName("localhost");
        alternativeNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(alternativeNames.Build());
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: true));

        var now = DateTimeOffset.UtcNow;
        using var generated = request.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(1));

        // Round-tripping through PKCS#12 persists the key, which is what SChannel needs before it
        // will use a certificate for server-side TLS on Windows.
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable);
    }
}

/// <summary>Serves a fixed certificate in place of the Key Vault factory.</summary>
internal sealed class FakeCertificateFactory(X509Certificate2 certificate) : ICertificateFactory
{
    public FakeCertificateFactory()
        : this(TestCertificate.Shared)
    {
    }

    public X509Certificate GetServerCertificate(ISessionContext sessionContext) => certificate;
}
