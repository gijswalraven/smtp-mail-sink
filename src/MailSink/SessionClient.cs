using System.Net;
using SmtpServer;
using SmtpServer.Net;

namespace MailSink;

/// <summary>
/// Reads the remote endpoint SmtpServer stashes in the session's property bag. Shared, because
/// the listener needs the address to cap connections, the authenticator needs it to make a
/// failed AUTH worth alerting on, and the capture needs it for the stored message.
/// </summary>
internal static class SessionClient
{
    /// <remarks>
    /// The context is nullable because a caller may have none -- SmtpServer hands the
    /// authenticator a real one, but a unit test exercising the comparison does not.
    /// </remarks>
    public static IPEndPoint? EndPoint(ISessionContext? context) =>
        context is not null &&
        context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var remote) &&
        remote is IPEndPoint endPoint
            ? endPoint
            : null;

    public static IPAddress? Address(ISessionContext? context) => EndPoint(context)?.Address;

    /// <summary>Address and port for a log line, or null when the session has no endpoint yet.</summary>
    public static string? Describe(ISessionContext? context) => EndPoint(context)?.ToString();
}
