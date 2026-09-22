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

/// <summary>
/// A real sink on a free port writing to a temp folder, so the SmtpServer wiring, capture
/// pipeline and file writer are all exercised. Shared by the end-to-end test classes, which
/// differ only in the settings they pass.
/// </summary>
internal sealed class TestSink : IAsyncDisposable
{
    private readonly IHost _host;

    private TestSink(IHost host, int port, string mailDirectory)
    {
        _host = host;
        Port = port;
        MailDirectory = mailDirectory;
    }

    public int Port { get; }

    public string MailDirectory { get; }

    public static async Task<TestSink> StartAsync(
        IDictionary<string, string?>? settings = null,
        bool production = false)
    {
        var mailDirectory = Path.Combine(
            Path.GetTempPath(), "mail-sink-tests", Guid.NewGuid().ToString("n"));
        var port = FreeTcpPort();

        var configuration = new Dictionary<string, string?>
        {
            ["MailSink:MailDirectory"] = mailDirectory,
            ["MailSink:ListenAddress"] = "127.0.0.1",
            ["MailSink:Port"] = port.ToString(),
            // Off unless a test asks for it: the default port would collide across the test
            // classes xunit runs in parallel, and outside Development that is a hard failure.
            ["MailSink:HealthPort"] = "0",
        };

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            configuration[key] = value;
        }

        // Development relaxes the credential rule; a test that wants the deployed rules asks for
        // Production and supplies its own.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = production ? Environments.Production : Environments.Development,
        });

        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddMailSink(builder.Configuration, builder.Environment);

        var host = builder.Build();
        await host.StartAsync();
        await WaitUntilListeningAsync(port);

        return new TestSink(host, port, mailDirectory);
    }

    public SmtpClient CreateClient() => new() { Timeout = 15000 };

    public async Task SendAsync(MailMessage message, NetworkCredential? credentials = null)
    {
        using var client = CreateClient();
        await client.ConnectAsync("127.0.0.1", Port, SecureSocketOptions.None);

        if (credentials is not null)
        {
            await client.AuthenticateAsync(credentials.UserName, credentials.Password);
        }

        await client.SendAsync(MimeMessage.CreateFromMailMessage(message));
        await client.DisconnectAsync(quit: true);
    }

    /// <summary>What the server advertises in EHLO.</summary>
    public async Task<SmtpCapabilities> CapabilitiesAsync()
    {
        using var client = CreateClient();
        await client.ConnectAsync("127.0.0.1", Port, SecureSocketOptions.None);

        var capabilities = client.Capabilities;

        await client.DisconnectAsync(quit: true);
        return capabilities;
    }

    /// <summary>Waits for the one .eml file the test expects and returns its path.</summary>
    public async Task<string> WaitForSingleFileAsync() => Assert.Single(await WaitForFilesAsync(1));

    /// <summary>
    /// Waits for <paramref name="count"/> .eml files anywhere under the mail directory and
    /// returns them, sorted, so a test can assert on the folders they landed in.
    /// </summary>
    public async Task<string[]> WaitForFilesAsync(int count)
    {
        // The SMTP reply comes after the write, but the directory listing can lag on Windows.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (Directory.Exists(MailDirectory))
            {
                var files = Directory.GetFiles(MailDirectory, "*.eml", SearchOption.AllDirectories);
                if (files.Length >= count)
                {
                    Array.Sort(files, StringComparer.Ordinal);
                    return files;
                }
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException(
            $"Fewer than {count} .eml file(s) appeared under {MailDirectory}.");
    }

    /// <summary>The path of <paramref name="file"/> relative to the mail directory, with '/'.</summary>
    public string RelativePathOf(string file) =>
        Path.GetRelativePath(MailDirectory, file).Replace(Path.DirectorySeparatorChar, '/');

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
    /// StartAsync returns once the hosted service has been started, not once it is accepting.
    /// Probing the socket keeps the first test connection from racing the listener.
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
