using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace MailSink;

/// <summary>
/// Answers <c>GET /healthz</c> with 200 while the SMTP listener is serving and 503 once it is not.
/// </summary>
/// <remarks>
/// <para>
/// This exists because Azure Container Instances only supports <c>exec</c> and <c>httpGet</c>
/// probes -- there is no TCP probe -- so without an HTTP endpoint nothing can distinguish a
/// running container from a wedged one. The same endpoint works for any other orchestrator.
/// </para>
/// <para>
/// A raw <see cref="TcpListener"/> rather than HttpListener, which needs a URL ACL or elevation
/// to bind anything but localhost on Windows. A probe endpoint is a fixed response to a fixed
/// path, so the amount of HTTP actually needed here is small enough to spell out.
/// </para>
/// </remarks>
public sealed class HealthEndpointService(
    IOptions<MailSinkOptions> options,
    SinkHealth health,
    IHostEnvironment environment,
    ILogger<HealthEndpointService> logger) : BackgroundService
{
    public const string Path = "/healthz";

    /// <summary>A probe that connects and then says nothing must not hold a task open.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly MailSinkOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.HealthPort <= 0)
        {
            return;
        }

        var listener = new TcpListener(SmtpOptionsFactory.ResolveAddress(_options), _options.HealthPort);

        try
        {
            listener.Start();
        }
        catch (SocketException ex) when (environment.IsDevelopment())
        {
            // Locally the port is just as likely to be some other project's, and losing the probe
            // does not stop the sink capturing mail. A deployment gets the opposite treatment:
            // the exception propagates and the host stops, because a container whose probe never
            // answers would be restarted forever.
            logger.LogWarning(
                ex,
                "Could not bind the health endpoint on port {Port}; continuing without it. Set MailSink:HealthPort to another port, or 0 to disable it.",
                _options.HealthPort);
            return;
        }

        logger.LogInformation(
            "health endpoint listening on {Address}:{Port}{Path}",
            _options.ListenAddress,
            _options.HealthPort,
            Path);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = RespondAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task RespondAsync(TcpClient client, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using (client)
            {
                await using var stream = client.GetStream();

                var buffer = new byte[1024];
                var read = await stream.ReadAsync(buffer, timeout.Token);
                var requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split('\r', '\n')[0];

                await stream.WriteAsync(Encoding.ASCII.GetBytes(Response(requestLine)), timeout.Token);
            }
        }
        catch (Exception)
        {
            // A probe that hangs up early, or a port scanner sending nothing, is not an event.
        }
    }

    internal string Response(string requestLine)
    {
        // "GET /healthz HTTP/1.1" -- anything else is not a request we answer.
        var parts = requestLine.Split(' ');
        var isHealthRequest = parts.Length >= 2
            && parts[0].Equals("GET", StringComparison.Ordinal)
            && parts[1].Split('?')[0].TrimEnd('/').Equals(Path.TrimEnd('/'), StringComparison.Ordinal);

        var (status, body) = (isHealthRequest, health.IsListening) switch
        {
            (false, _) => ("404 Not Found", "not found"),
            (true, true) => ("200 OK", "ok"),
            (true, false) => ("503 Service Unavailable", "not listening"),
        };

        return $"HTTP/1.1 {status}\r\n" +
               "Content-Type: text/plain; charset=utf-8\r\n" +
               $"Content-Length: {body.Length}\r\n" +
               "Connection: close\r\n" +
               "\r\n" +
               body;
    }
}
