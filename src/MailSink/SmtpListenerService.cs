using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServerCore = SmtpServer.SmtpServer;

namespace MailSink;

/// <summary>Hosts the SMTP listener for the lifetime of the process.</summary>
public sealed class SmtpListenerService(
    IOptions<MailSinkOptions> options,
    IMailWriter writer,
    IServiceProvider serviceProvider,
    ILogger<SmtpListenerService> logger) : BackgroundService
{
    private readonly MailSinkOptions _options = options.Value;

    /// <summary>
    /// Sessions currently being served. A set keyed on the session rather than a counter, because
    /// a session ends through exactly one of three events and removing a key twice is harmless --
    /// a counter would drift out of step the first time that assumption failed.
    /// </summary>
    private readonly ConcurrentDictionary<ISessionContext, byte> _sessions = new();

    private SmtpServerCore? _server;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _server = new SmtpServerCore(SmtpOptionsFactory.Build(_options), serviceProvider);

        _server.SessionCreated += OnSessionCreated;
        _server.SessionCompleted += OnSessionEnded;
        _server.SessionCancelled += OnSessionEnded;
        _server.SessionFaulted += (_, e) =>
        {
            OnSessionEnded(null, e);
            logger.LogWarning(e.Exception, "SMTP session faulted");
        };

        logger.LogInformation(
            "mail sink listening on {Address} port(s) {Ports}; auth: {Auth}; writing .eml files to {Destination}",
            _options.ListenAddress,
            string.Join(", ", SmtpOptionsFactory.ResolvePorts(_options)),
            DescribeAuthentication(_options),
            writer.Destination);

        try
        {
            await _server.StartAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// One line for the startup log, so a misconfigured credential pair shows up immediately
    /// rather than as an unexplained 535 in the sending application.
    /// </summary>
    private static string DescribeAuthentication(MailSinkOptions options)
    {
        var offer = options switch
        {
            { HasFixedCredentials: true } => $"credentials for '{options.Username}'",
            { AllowAnyCredentials: true } => "any credentials",
            _ => "none advertised",
        };

        return options.RequireAuthentication ? $"{offer}, required" : $"{offer}, optional";
    }

    /// <summary>
    /// Drops the connection when the sink is already serving as many sessions as it is willing to.
    /// Without this, every concurrent sender can pin another MaxMessageSize of memory.
    /// </summary>
    private void OnSessionCreated(object? sender, SessionEventArgs e)
    {
        _sessions.TryAdd(e.Context, 0);

        if (_options.MaxConcurrentSessions <= 0 || _sessions.Count <= _options.MaxConcurrentSessions)
        {
            return;
        }

        _sessions.TryRemove(e.Context, out _);
        logger.LogWarning(
            "Refusing session {SessionId}: already at the MaxConcurrentSessions limit of {Limit}",
            e.Context.SessionId,
            _options.MaxConcurrentSessions);

        // Closing the pipe is the only way to turn a session away at this point; the session then
        // ends through SessionFaulted, which is expected here rather than a problem.
        (e.Context.Pipe as IDisposable)?.Dispose();
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e) => _sessions.TryRemove(e.Context, out _);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _server?.Shutdown();
        await base.StopAsync(cancellationToken);
    }
}
