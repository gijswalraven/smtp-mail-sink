using System.Security.Authentication;

namespace MailSink;

/// <summary>A TLS version, used as both the floor and the ceiling of what is offered.</summary>
public enum TlsProtocolVersion
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
    public TlsProtocolVersion MinimumProtocol { get; set; } = TlsProtocolVersion.Tls12;

    /// <summary>
    /// Highest TLS version a client may negotiate. 1.3 by default, so the newest both ends
    /// support is used.
    /// </summary>
    /// <remarks>
    /// Set to Tls12 to take 1.3 off the table. That is a diagnostic lever rather than a hardening
    /// one -- 1.3 is the better protocol -- but some client TLS stacks abort a 1.3 handshake in
    /// ways that look like a network fault from the server side, and the only cheap way to rule
    /// that out is to stop offering it.
    /// </remarks>
    public TlsProtocolVersion MaximumProtocol { get; set; } = TlsProtocolVersion.Tls13;

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
    public SslProtocols Protocols => (MinimumProtocol, MaximumProtocol) switch
    {
        (TlsProtocolVersion.Tls13, _) => SslProtocols.Tls13,
        (_, TlsProtocolVersion.Tls12) => SslProtocols.Tls12,
        _ => SslProtocols.Tls12 | SslProtocols.Tls13,
    };

    /// <summary>True when the range is the wrong way round and would offer nothing.</summary>
    public bool IsProtocolRangeInverted =>
        MinimumProtocol == TlsProtocolVersion.Tls13 && MaximumProtocol == TlsProtocolVersion.Tls12;
}
