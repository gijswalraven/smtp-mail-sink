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
    /// Plain-text ports to bind, de-duplicated. Falls back to
    /// <see cref="MailSinkOptions.DefaultPort"/> when none are configured - the options default is
    /// deliberately empty because the configuration binder appends to array defaults instead of
    /// replacing them.
    /// </summary>
    public static int[] ResolvePorts(MailSinkOptions options) =>
        options.Ports.Length == 0
            ? [MailSinkOptions.DefaultPort]
            : [.. options.Ports.Distinct()];

    /// <summary>
    /// The two TLS port sets. Configuring either one takes the defaults off the table entirely,
    /// so asking for implicit TLS alone does not silently also open 587.
    /// </summary>
    public static (int[] StartTls, int[] ImplicitTls) ResolveTlsPorts(MailSinkOptions options) =>
        options.StartTlsPorts.Length == 0 && options.ImplicitTlsPorts.Length == 0
            ? ([MailSinkOptions.DefaultStartTlsPort], [MailSinkOptions.DefaultImplicitTlsPort])
            : ([.. options.StartTlsPorts.Distinct()], [.. options.ImplicitTlsPorts.Distinct()]);

    public static IPAddress ResolveAddress(MailSinkOptions options) =>
        IPAddress.TryParse(options.ListenAddress, out var address)
            ? address
            : throw new InvalidOperationException(
                $"MailSink:ListenAddress '{options.ListenAddress}' is not a valid IP address.");

    /// <summary>True when the sink should listen in plain text rather than terminate TLS.</summary>
    /// <remarks>
    /// Only ever true in Development -- <see cref="MailSinkOptions.Validate"/> refuses to start
    /// anywhere else without a certificate. A developer who does configure one gets TLS locally
    /// too, which is how the TLS path is exercised without deploying.
    /// </remarks>
    public static bool IsPlainText(MailSinkOptions options, bool isDevelopment) =>
        isDevelopment && !options.Tls.IsConfigured;

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
            foreach (var port in ResolvePorts(options))
            {
                AddEndpoint(builder, options, address, port, isSecure: false, certificateFactory: null);
            }

            return builder.Build();
        }

        if (certificateFactory is null)
        {
            throw new InvalidOperationException(
                "TLS is required here but no certificate was supplied. This is a wiring mistake: " +
                $"{nameof(MailSinkServiceCollectionExtensions.AddMailSink)} registers an " +
                $"{nameof(ICertificateFactory)} whenever MailSink:Tls:KeyVaultCertificateUri is set.");
        }

        var (startTlsPorts, implicitTlsPorts) = ResolveTlsPorts(options);

        foreach (var port in startTlsPorts)
        {
            AddEndpoint(builder, options, address, port, isSecure: false, certificateFactory);
        }

        foreach (var port in implicitTlsPorts)
        {
            AddEndpoint(builder, options, address, port, isSecure: true, certificateFactory);
        }

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
