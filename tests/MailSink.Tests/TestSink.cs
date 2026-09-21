using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using MailSink;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MailSink.Tests;

/// <summary>
/// A real sink on a free port writing to a temp folder, so the SmtpServer wiring, capture
/// pipeline and file writer are all exercised. Shared by the end-to-end test classes, which differ
/// only in the settings they pass.
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

    public static async Task<TestSink> StartAsync(IDictionary<string, string?>? settings = null)
    {
        var mailDirectory = Path.Combine(
            Path.GetTempPath(), "mail-sink-tests", Guid.NewGuid().ToString("n"));
        var port = FreeTcpPort();

        var configuration = new Dictionary<string, string?>
        {
            ["MailSink:MailDirectory"] = mailDirectory,
            ["MailSink:ListenAddress"] = "127.0.0.1",
            ["MailSink:Ports:0"] = port.ToString(),
        };

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            configuration[key] = value;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddMailSink(builder.Configuration);

        var host = builder.Build();
        await host.StartAsync();

        return new TestSink(host, port, mailDirectory);
    }

    public SmtpClient CreateClient(NetworkCredential? credentials = null) =>
        new("127.0.0.1", Port) { Timeout = 15000, Credentials = credentials };

    public void Send(MailMessage message, NetworkCredential? credentials = null)
    {
        using var client = CreateClient(credentials);
        client.Send(message);
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

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
