using System.Net;
using SmtpServer;

namespace MailSink;

/// <summary>
/// Turns <see cref="MailSinkOptions"/> into SmtpServer's options. Kept separate from the hosted
/// service so the port and address rules can be asserted without opening a socket.
/// </summary>
public static class SmtpOptionsFactory
{
    /// <summary>
    /// Ports to bind, de-duplicated. Falls back to <see cref="MailSinkOptions.DefaultPort"/> when
    /// none are configured - the options default is deliberately empty because the configuration
    /// binder appends to array defaults instead of replacing them.
    /// </summary>
    public static int[] ResolvePorts(MailSinkOptions options) =>
        options.Ports.Length == 0
            ? [MailSinkOptions.DefaultPort]
            : [.. options.Ports.Distinct()];

    public static IPAddress ResolveAddress(MailSinkOptions options) =>
        IPAddress.TryParse(options.ListenAddress, out var address)
            ? address
            : throw new InvalidOperationException(
                $"MailSink:ListenAddress '{options.ListenAddress}' is not a valid IP address.");

    public static ISmtpServerOptions Build(MailSinkOptions options)
    {
        var address = ResolveAddress(options);
        options.ValidateCredentials();

        var builder = new SmtpServerOptionsBuilder()
            .ServerName(options.ServerName)
            .MaxMessageSize(options.MaxMessageSize, MaxMessageSizeHandling.Strict);

        foreach (var port in ResolvePorts(options))
        {
            builder.Endpoint(endpoint => endpoint
                .Endpoint(new IPEndPoint(address, port))
                .IsSecure(false)
                .AuthenticationRequired(options.RequireAuthentication)
                // No TLS here, so AUTH is only offered at all if unsecure authentication is
                // allowed -- which is the point of a sink on a trusted network.
                .AllowUnsecureAuthentication(options.OffersAuthentication));
        }

        return builder.Build();
    }
}
