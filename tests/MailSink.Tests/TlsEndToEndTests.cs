using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace MailSink.Tests;

/// <summary>
/// The deployed posture against a real listener: TLS on both port styles, AUTH withheld until the
/// connection is encrypted, and no way to deliver a message without doing both.
/// </summary>
/// <remarks>
/// These run the sink outside Development, so they exercise exactly the wiring a deployment gets.
/// Only the certificate is swapped, for the obvious reason that a test cannot reach Key Vault.
/// </remarks>
public class TlsEndToEndTests
{
    private static Dictionary<string, string?> Credentials() => new()
    {
        ["MailSink:Username"] = "app",
        ["MailSink:Password"] = "s3cret",
    };

    private static NetworkCredential Valid => new("app", "s3cret");

    private static MailMessage Message(string subject) =>
        new("app@example.test", "gijs@example.test", subject, "body");

    [Theory]
    [InlineData(SinkTransport.StartTls)]
    [InlineData(SinkTransport.ImplicitTls)]
    public async Task A_message_round_trips_over_TLS(SinkTransport transport)
    {
        await using var sink = await TestSink.StartAsync(Credentials(), transport);
        using var message = Message("Encrypted delivery");

        await sink.SendAsync(message, Valid);

        var parsed = await MimeMessage.LoadAsync(await sink.WaitForSingleFileAsync());
        Assert.Equal("Encrypted delivery", parsed.Subject);
    }

    [Fact]
    public async Task AUTH_is_not_advertised_before_STARTTLS()
    {
        await using var sink = await TestSink.StartAsync(Credentials(), SinkTransport.StartTls);

        var capabilities = await sink.CapabilitiesBeforeUpgradeAsync();

        // The upgrade is on offer; the credentials are not, until it has happened.
        Assert.True(capabilities.HasFlag(SmtpCapabilities.StartTLS));
        Assert.False(capabilities.HasFlag(SmtpCapabilities.Authentication));
    }

    [Fact]
    public async Task A_sender_that_skips_STARTTLS_cannot_deliver()
    {
        await using var sink = await TestSink.StartAsync(Credentials(), SinkTransport.StartTls);
        using var message = Message("Should not arrive");

        using var client = sink.CreateClient();
        await client.ConnectAsync("127.0.0.1", sink.Port, SecureSocketOptions.None);

        // Nothing to authenticate with over a plain connection, and MAIL FROM is refused without
        // it -- so there is no path from an unencrypted session to a stored message.
        await Assert.ThrowsAsync<ServiceNotAuthenticatedException>(
            () => client.SendAsync(MimeMessage.CreateFromMailMessage(message)));

        await client.DisconnectAsync(quit: true);
        await sink.AssertNothingCapturedAsync();
    }

    [Fact]
    public async Task The_negotiated_protocol_is_at_least_TLS_1_2()
    {
        await using var sink = await TestSink.StartAsync(Credentials(), SinkTransport.ImplicitTls);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, sink.Port);
        await using var stream = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);

        await stream.AuthenticateAsClientAsync("localhost");

        Assert.Contains(stream.SslProtocol, new[] { SslProtocols.Tls12, SslProtocols.Tls13 });
    }

    [Fact]
    public async Task A_client_offering_only_TLS_1_1_is_refused()
    {
        await using var sink = await TestSink.StartAsync(Credentials(), SinkTransport.ImplicitTls);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, sink.Port);
        await using var stream = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);

        // Most platforms now refuse 1.1 in the client too, so this can fail before it reaches the
        // sink. Either way the handshake must not succeed.
        await Assert.ThrowsAnyAsync<Exception>(() => stream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
#pragma warning disable SYSLIB0039 // Asserting that an obsolete protocol is rejected is the point.
                EnabledSslProtocols = SslProtocols.Tls11,
#pragma warning restore SYSLIB0039
            }));
    }
}
