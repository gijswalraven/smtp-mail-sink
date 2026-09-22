using System.Net;
using SmtpServer;

namespace MailSink;

/// <summary>
/// Turns <see cref="MailSinkOptions"/> into SmtpServer's options. Kept separate from the hosted
/// service so the port, address and TLS rules can be asserted without opening a socket.
/// </summary>
public static class SmtpOptionsFactory
{
    /// <summary>
    /// The single port to bind. Zero means "the conventional port for the mode": 587 for
    /// STARTTLS, 465 for implicit TLS, 1025 for the Development plain-text listener.
    /// </summary>
    /// <remarks>
    /// Keyed on what the listener actually is rather than on TlsMode alone. A Development run
    /// with no certificate leaves TlsMode at its StartTls default but listens in plain text, and
    /// resolving that to 587 put the sink on the submission port while the compose file, the
    /// Dockerfile and the docs all said 1025.
    /// </remarks>
    public static int ResolvePort(MailSinkOptions options, bool isDevelopment) =>
        options.Port != 0
            ? options.Port
            : IsPlainText(options, isDevelopment)
                ? MailSinkOptions.DefaultPort
                : options.TlsMode switch
                {
                    SmtpTlsMode.Implicit => MailSinkOptions.DefaultImplicitTlsPort,
                    SmtpTlsMode.None => MailSinkOptions.DefaultPort,
                    _ => MailSinkOptions.DefaultStartTlsPort,
                };

    public static IPAddress ResolveAddress(MailSinkOptions options) =>
        IPAddress.TryParse(options.ListenAddress, out var address)
            ? address
            : throw new InvalidOperationException(
                $"MailSink:ListenAddress '{options.ListenAddress}' is not a valid IP address.");

    /// <summary>True when the sink should listen in plain text rather than terminate TLS.</summary>
    /// <remarks>
    /// Only ever true in Development -- <see cref="MailSinkOptions.Validate"/> refuses both
    /// <see cref="SmtpTlsMode.None"/> and a missing certificate anywhere else. An unconfigured
    /// certificate still means plain text locally, so "compose up" needs no TLS settings at all;
    /// a developer who does configure one gets TLS locally too, which is how the TLS path is
    /// exercised without deploying.
    /// </remarks>
    public static bool IsPlainText(MailSinkOptions options, bool isDevelopment) =>
        options.IsPlainText(isDevelopment);

    public static ISmtpServerOptions Build(
        MailSinkOptions options,
        bool isDevelopment,
        ICertificateFactory? certificateFactory)
    {
        var address = ResolveAddress(options);
        options.Validate(isDevelopment);

        var builder = new SmtpServerOptionsBuilder()
            .ServerName(options.ServerName)
            .MaxMessageSize(options.MaxMessageSize, MaxMessageSizeHandling.Strict)
            .MaxAuthenticationAttempts(options.MaxAuthenticationAttempts)
            .CommandWaitTimeout(options.CommandWaitTimeout);

        if (IsPlainText(options, isDevelopment))
        {
            AddEndpoint(builder, options, address, ResolvePort(options, isDevelopment), isSecure: false, certificateFactory: null);
            return builder.Build();
        }

        if (certificateFactory is null)
        {
            throw new InvalidOperationException(
                "TLS is required here but no certificate was supplied. This is a wiring mistake: " +
                $"{nameof(MailSinkServiceCollectionExtensions.AddMailSink)} registers an " +
                $"{nameof(ICertificateFactory)} whenever MailSink:Tls:KeyVaultCertificateUri is set.");
        }

        // IsSecure is the whole difference between the two TLS modes: true hands the endpoint a
        // TLS stream from the first byte, false leaves it in plain text until STARTTLS upgrades it.
        AddEndpoint(
            builder,
            options,
            address,
            ResolvePort(options, isDevelopment),
            isSecure: options.TlsMode == SmtpTlsMode.Implicit,
            certificateFactory);

        return builder.Build();
    }

    private static void AddEndpoint(
        SmtpServerOptionsBuilder builder,
        MailSinkOptions options,
        IPAddress address,
        int port,
        bool isSecure,
        ICertificateFactory? certificateFactory)
    {
        builder.Endpoint(endpoint =>
        {
            endpoint
                .Endpoint(new IPEndPoint(address, port))
                .IsSecure(isSecure)
                .AuthenticationRequired(options.HasCredentials)
                .SessionTimeout(options.SessionTimeout)
                // Credentials are only ever invited across an encrypted connection. On a STARTTLS
                // port that means AUTH is absent from the first EHLO and appears in the second;
                // combined with AuthenticationRequired, a session that skips STARTTLS cannot get
                // past MAIL FROM. The exception is a Development plain-text port, where there is
                // no certificate to upgrade to.
                .AllowUnsecureAuthentication(certificateFactory is null);

            if (certificateFactory is not null)
            {
                endpoint
                    .Certificate(certificateFactory)
                    .SupportedSslProtocols(options.Tls.Protocols);
            }
        });
    }
}
