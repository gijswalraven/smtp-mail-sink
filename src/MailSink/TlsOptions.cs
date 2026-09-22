using System.Security.Authentication;

namespace MailSink;

/// <summary>The lowest TLS version a client may negotiate.</summary>
public enum TlsProtocolFloor
{
    /// <summary>TLS 1.2 and 1.3. The default: 1.3 alone still turns away real clients.</summary>
    Tls12,

    /// <summary>TLS 1.3 only.</summary>
    Tls13,
}

/// <summary>How the listener terminates TLS. One port, one mode.</summary>
/// <remarks>
/// STARTTLS and implicit TLS cannot share a port: implicit TLS has the client open with a TLS
/// handshake, STARTTLS has the server open with a plain-text greeting, and nothing can answer
/// both without sniffing the first byte. So the mode is configuration rather than a second
/// listener, and a sender is told which one to use.
/// </remarks>
public enum SmtpTlsMode
{
    /// <summary>Starts in plain text; STARTTLS is required before AUTH. The submission default.</summary>
    StartTls,

    /// <summary>TLS from the first byte.</summary>
    Implicit,

    /// <summary>No TLS at all. <b>Development only</b>; refused anywhere else.</summary>
    None,
}

/// <summary>
/// TLS settings, bound from the "MailSink:Tls" configuration section. Required outside
/// Development, where the sink listens in plain text and loads no certificate at all.
/// </summary>
public sealed class TlsOptions
{
    /// <summary>
    /// The certificate to present, as a Key Vault certificate URI, e.g.
    /// <c>https://kv-mailsink-1a2b.vault.azure.net/certificates/mailsink-smtp</c>. A version can be
    /// pinned by appending one; leaving it off means a rotation is picked up without a redeploy.
    /// The host must appear in <see cref="KeyVaultOptions.AllowedHosts"/> when that list is set.
    /// </summary>
    public string KeyVaultCertificateUri { get; set; } = string.Empty;

    /// <summary>Lowest TLS version a client may negotiate. Nothing below 1.2 is offered.</summary>
    public TlsProtocolFloor MinimumProtocol { get; set; } = TlsProtocolFloor.Tls12;

    /// <summary>
    /// How long a fetched certificate is reused before Key Vault is asked again. This is what
    /// makes a rotation take effect on a running sink, so it trades staleness for vault calls
    /// rather than correctness.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>True once a certificate has been configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(KeyVaultCertificateUri);

    /// <summary>
    /// The protocol set handed to SmtpServer. Deliberately built from an enum rather than bound
    /// straight onto <see cref="SslProtocols"/>: that would let configuration switch TLS 1.0 back
    /// on, and there is no reason to allow it.
    /// </summary>
    public SslProtocols Protocols => MinimumProtocol == TlsProtocolFloor.Tls13
        ? SslProtocols.Tls13
        : SslProtocols.Tls12 | SslProtocols.Tls13;
}
