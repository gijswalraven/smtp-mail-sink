using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServerCore = SmtpServer.SmtpServer;

namespace MailSink;

/// <summary>Hosts the SMTP listener for the lifetime of the process.</summary>
public sealed class SmtpListenerService(
    IOptions<MailSinkOptions> options,
    IMailWriter writer,
    SinkHealth health,
    IHostEnvironment environment,
    IServiceProvider serviceProvider,
    ILogger<SmtpListenerService> logger) : BackgroundService
{
    private readonly MailSinkOptions _options = options.Value;

    /// <summary>
    /// Sessions currently being served, each against the address it came from. A map keyed on the
    /// session rather than a counter, because a session ends through exactly one of three events
    /// and removing a key twice is harmless -- a counter would drift out of step the first time
    /// that assumption failed. The per-client tally is derived by scanning the values, which is
    /// cheap at these sizes and cannot fall out of step with the sessions it counts.
    /// </summary>
    private readonly ConcurrentDictionary<ISessionContext, IPAddress?> _sessions = new();

    private SmtpServerCore? _server;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var isDevelopment = environment.IsDevelopment();

        _server = new SmtpServerCore(
            SmtpOptionsFactory.Build(_options, isDevelopment),
            serviceProvider);

        _server.SessionCreated += OnSessionCreated;
        _server.SessionCompleted += OnSessionEnded;
        _server.SessionCancelled += OnSessionEnded;
        _server.SessionFaulted += (_, e) =>
        {
            var client = SessionClient.Address(e.Context)?.ToString() ?? "an unknown address";
            OnSessionEnded(null, e);
            logger.LogWarning(
                e.Exception,
                "SMTP session {SessionId} from {Client} faulted",
                e.Context.SessionId,
                client);
        };

        logger.LogInformation(
            "mail sink listening on {Address}; {Transport}; auth: {Auth}; storing .eml in {Destination}",
            _options.ListenAddress,
            DescribeTransport(_options),
            DescribeAuthentication(_options),
            writer.Destination);

        // Every connection to this sink is plain text, so this is not a conditional warning any
        // more: it is what the sink is. Said on every start so it cannot become invisible.
        logger.LogWarning(
            "This sink is not encrypted. Mail{Credentials} crosses the network in clear text and " +
            "can be read or altered in transit. Put it only where that traffic is already " +
            "trusted; see SECURITY.md.",
            _options.HasCredentials
                ? ", and the password every sender authenticates with,"
                : " is accepted from anyone and");

        try
        {
            health.MarkListening();
            await _server.StartAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            // Bare "Permission denied" from deep inside the library says nothing about which port
            // or why. Ports below 1024 are privileged, and whether a non-root container may bind
            // one depends on the runtime: Docker allows it, Azure Container Instances does not.
            // Diagnosing that from the raw exception cost an afternoon; say it plainly instead.
            throw new InvalidOperationException(
                $"Could not bind {DescribeTransport(_options)} as this user. Ports " +
                "below 1024 are privileged, and not every container runtime lets a non-root " +
                "process bind one -- Azure Container Instances does not. Move the listener above " +
                "1024 with MailSink:Port, and publish or forward the port senders should see.",
                ex);
        }
        finally
        {
            // Whatever ended the loop -- shutdown, a fault, a bind failure -- the sink is no
            // longer taking mail, and the health endpoint has to start saying so.
            health.MarkStopped();
        }
    }

    /// <summary>One line for the startup log, so the transport in force is never in doubt.</summary>
    private static string DescribeTransport(MailSinkOptions options) =>
        $"plain text on port {SmtpOptionsFactory.ResolvePort(options)}, no TLS";

    /// <summary>
    /// One line for the startup log, so a misconfigured credential pair shows up immediately
    /// rather than as an unexplained 535 in the sending application.
    /// </summary>
    private static string DescribeAuthentication(MailSinkOptions options)
    {
        var accounts = options.ResolveAccounts();

        return accounts.Count switch
        {
            0 => "none -- mail is accepted from anyone",
            1 => $"required, credentials for '{accounts[0].Key}'{DescribeFolder(accounts[0])}",
            _ => $"required, {accounts.Count} accounts: " +
                 string.Join(", ", accounts.Select(account => account.Key + DescribeFolder(account))),
        };

        // The folder is worth a startup log line of its own: it is the difference between mail
        // that is missing and mail that is somewhere else.
        static string DescribeFolder(ResolvedAccount account) =>
            account.Folder.Length == 0 ? string.Empty : $" -> {account.Folder}/";
    }

    /// <summary>
    /// Drops the connection when the sink is already serving as many sessions as it is willing
    /// to, either overall or from this one client. Without the global cap, every concurrent
    /// sender can pin another MaxMessageSize of memory; without the per-client one, a single host
    /// can take the whole budget and starve everyone else.
    /// </summary>
    private void OnSessionCreated(object? sender, SessionEventArgs e)
    {
        var address = SessionClient.Address(e.Context);
        _sessions[e.Context] = address;

        // Before the greeting, so a client that connects and then gives up is distinguishable
        // from one that never reached the sink at all. Telling those two apart is most of
        // diagnosing a sender that "just fails".
        logger.LogInformation(
            "session {SessionId} accepted from {Client}",
            e.Context.SessionId,
            address?.ToString() ?? "an unknown address");

        if (!ExceedsLimit(address, out var reason))
        {
            return;
        }

        _sessions.TryRemove(e.Context, out _);
        logger.LogWarning(
            "Refusing session {SessionId} from {Client}: {Reason}",
            e.Context.SessionId,
            address?.ToString() ?? "an unknown address",
            reason);

        // Closing the pipe is the only way to turn a session away at this point; the session then
        // ends through SessionFaulted, which is expected here rather than a problem.
        (e.Context.Pipe as IDisposable)?.Dispose();
    }

    private bool ExceedsLimit(IPAddress? address, out string reason)
    {
        if (_options.MaxConcurrentSessions > 0 && _sessions.Count > _options.MaxConcurrentSessions)
        {
            reason = $"already at the MaxConcurrentSessions limit of {_options.MaxConcurrentSessions}";
            return true;
        }

        if (address is not null && _options.MaxSessionsPerClient > 0)
        {
            var fromClient = _sessions.Count(session => address.Equals(session.Value));
            if (fromClient > _options.MaxSessionsPerClient)
            {
                reason = $"already at the MaxSessionsPerClient limit of {_options.MaxSessionsPerClient} for this client";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e) => _sessions.TryRemove(e.Context, out _);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _server?.Shutdown();
        await base.StopAsync(cancellationToken);
    }
}
