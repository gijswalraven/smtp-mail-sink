using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailSink;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MimeKit;
using SmtpServer;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

namespace MailSink.Tests;

/// <summary>How a <see cref="TestSink"/> listens, and so how a client must connect to it.</summary>
public enum SinkTransport
{
    /// <summary>Plain text, the Development-only mode. Credentials are optional here.</summary>
    PlainText,

    /// <summary>Port 587 style: plain connect, STARTTLS before AUTH.</summary>
    StartTls,

    /// <summary>Port 465 style: TLS from the first byte.</summary>
    ImplicitTls,
}

/// <summary>
/// A real sink on a free port writing to a temp folder, so the SmtpServer wiring, capture
/// pipeline and file writer are all exercised. Shared by the end-to-end test classes, which differ
/// only in the transport and settings they pass.
/// </summary>
internal sealed class TestSink : IAsyncDisposable
{
    /// <summary>
    /// A URI that only has to parse and pass the allow-list: the factory that would fetch it is
    /// replaced below, because a test cannot reach Key Vault. Its presence is what tells
    /// <see cref="MailSinkOptions.Validate"/> that TLS is configured.
    /// </summary>
    private const string CertificateUri =
        "https://kv-mailsink-test.vault.azure.net/certificates/mailsink-smtp";

    private readonly IHost _host;

    private TestSink(IHost host, int port, string mailDirectory, SinkTransport transport)
    {
        _host = host;
        Port = port;
        MailDirectory = mailDirectory;
        Transport = transport;
    }

    public int Port { get; }

    public string MailDirectory { get; }

    public SinkTransport Transport { get; }

    public static async Task<TestSink> StartAsync(
        IDictionary<string, string?>? settings = null,
        SinkTransport transport = SinkTransport.PlainText)
    {
        var mailDirectory = Path.Combine(
            Path.GetTempPath(), "mail-sink-tests", Guid.NewGuid().ToString("n"));
        var port = FreeTcpPort();

        var configuration = new Dictionary<string, string?>
        {
            ["MailSink:MailDirectory"] = mailDirectory,
            ["MailSink:ListenAddress"] = "127.0.0.1",
            [PortKey(transport)] = port.ToString(),
            // Off unless a test asks for it: the default port would collide across the test
            // classes xunit runs in parallel, and outside Development that is a hard failure.
            ["MailSink:HealthPort"] = "0",
        };

        if (transport != SinkTransport.PlainText)
        {
            configuration["MailSink:Tls:KeyVaultCertificateUri"] = CertificateUri;
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            configuration[key] = value;
        }

        // Plain text is only legal in Development; everything else runs as the deployed sink does,
        // so the strict validation and the TLS wiring are what the tests actually exercise.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = transport == SinkTransport.PlainText
                ? Environments.Development
                : Environments.Production,
        });

        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddMailSink(builder.Configuration, builder.Environment);

        if (transport != SinkTransport.PlainText)
        {
            // Swap the Key Vault factory for a local certificate. Everything downstream of it --
            // endpoint wiring, protocol floor, STARTTLS negotiation -- is the production path.
            builder.Services.RemoveAll<ICertificateFactory>();
            builder.Services.AddSingleton<ICertificateFactory>(
                new FakeCertificateFactory(TestCertificate.Shared));
        }

        var host = builder.Build();
        await host.StartAsync();
        await WaitUntilListeningAsync(port);

        return new TestSink(host, port, mailDirectory, transport);
    }

    private static string PortKey(SinkTransport transport) => transport switch
    {
        SinkTransport.PlainText => "MailSink:Ports:0",
        SinkTransport.StartTls => "MailSink:StartTlsPorts:0",
        SinkTransport.ImplicitTls => "MailSink:ImplicitTlsPorts:0",
        _ => throw new ArgumentOutOfRangeException(nameof(transport)),
    };

    private SecureSocketOptions SocketOptions => Transport switch
    {
        SinkTransport.PlainText => SecureSocketOptions.None,
        SinkTransport.StartTls => SecureSocketOptions.StartTls,
        SinkTransport.ImplicitTls => SecureSocketOptions.SslOnConnect,
        _ => throw new ArgumentOutOfRangeException(nameof(Transport)),
    };

    /// <summary>
    /// A client that trusts the test certificate. MailKit takes the callback per instance, unlike
    /// ServicePointManager, which matters because xunit runs these classes in parallel.
    /// </summary>
    public SmtpClient CreateClient() => new()
    {
        ServerCertificateValidationCallback = (_, _, _, _) => true,
        Timeout = 15000,
    };

    public async Task SendAsync(MailMessage message, NetworkCredential? credentials = null)
    {
        using var client = CreateClient();
        await client.ConnectAsync("127.0.0.1", Port, SocketOptions);

        if (credentials is not null)
        {
            await client.AuthenticateAsync(credentials.UserName, credentials.Password);
        }

        await client.SendAsync(MimeMessage.CreateFromMailMessage(message));
        await client.DisconnectAsync(quit: true);
    }

    /// <summary>
    /// What the server advertises in the very first EHLO, before any TLS upgrade. Used to prove
    /// that AUTH is not on offer across an unencrypted connection.
    /// </summary>
    public async Task<SmtpCapabilities> CapabilitiesBeforeUpgradeAsync()
    {
        using var client = CreateClient();
        await client.ConnectAsync("127.0.0.1", Port, SecureSocketOptions.None);

        var capabilities = client.Capabilities;

        await client.DisconnectAsync(quit: true);
        return capabilities;
    }

    /// <summary>Waits for the one .eml file the test expects and returns its path.</summary>
    public async Task<string> WaitForSingleFileAsync()
    {
        // The SMTP reply comes after the write, but the directory listing can lag on Windows.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (Directory.Exists(MailDirectory))
            {
                var files = Directory.GetFiles(MailDirectory, "*.eml", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    return Assert.Single(files);
                }
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"No .eml file appeared under {MailDirectory}.");
    }

    /// <summary>
    /// Asserts nothing was captured. Waits first, so a message that is merely slow does not pass
    /// as a message that was refused.
    /// </summary>
    public async Task AssertNothingCapturedAsync()
    {
        await Task.Delay(500);

        var files = Directory.Exists(MailDirectory)
            ? Directory.GetFiles(MailDirectory, "*.eml", SearchOption.AllDirectories)
            : [];

        Assert.Empty(files);
    }

    /// <summary>A port nothing is listening on, for a caller that needs to configure one.</summary>
    public static int FreePort() => FreeTcpPort();

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// StartAsync returns once the hosted service has been started, not once it is accepting, and
    /// a TLS handshake widens that gap. Probing the socket keeps the first test connection from
    /// racing the listener.
    /// </summary>
    private static async Task WaitUntilListeningAsync(int port)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }

        throw new Xunit.Sdk.XunitException($"The sink never started listening on port {port}.");
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();

        if (Directory.Exists(MailDirectory))
        {
            Directory.Delete(MailDirectory, recursive: true);
        }
    }
}
