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
    /// The port to bind. Zero, the default, means <see cref="MailSinkOptions.DefaultPort"/>.
    /// </summary>
    public static int ResolvePort(MailSinkOptions options) =>
        options.Port != 0 ? options.Port : MailSinkOptions.DefaultPort;

    public static IPAddress ResolveAddress(MailSinkOptions options) =>
        IPAddress.TryParse(options.ListenAddress, out var address)
            ? address
            : throw new InvalidOperationException(
                $"MailSink:ListenAddress '{options.ListenAddress}' is not a valid IP address.");

    public static ISmtpServerOptions Build(MailSinkOptions options, bool isDevelopment)
    {
        var address = ResolveAddress(options);
        options.Validate(isDevelopment);

        var builder = new SmtpServerOptionsBuilder()
            .ServerName(options.ServerName)
            .MaxMessageSize(options.MaxMessageSize, MaxMessageSizeHandling.Strict)
            .MaxAuthenticationAttempts(options.MaxAuthenticationAttempts)
            .CommandWaitTimeout(options.CommandWaitTimeout);

        builder.Endpoint(endpoint => endpoint
            .Endpoint(new IPEndPoint(address, ResolvePort(options)))
            .IsSecure(false)
            .AuthenticationRequired(options.HasCredentials)
            .SessionTimeout(options.SessionTimeout)
            // The connection is never encrypted, so AUTH has to be offered over it or the
            // credentials could not be used at all. That is the trade this sink now makes:
            // see SECURITY.md before putting it anywhere its traffic is not already trusted.
            .AllowUnsecureAuthentication(true));

        return builder.Build();
    }
}
