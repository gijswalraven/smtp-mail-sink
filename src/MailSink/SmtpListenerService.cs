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
        var certificateFactory = serviceProvider.GetService<ICertificateFactory>();

        // Pull the certificate down before the first client rather than during its handshake, so
        // an unreachable vault or a certificate without a private key fails the host with a clear
        // error instead of showing up as a TLS reset.
        if (certificateFactory is KeyVaultCertificateFactory keyVaultCertificates)
        {
            keyVaultCertificates.Current();
        }

        _server = new SmtpServerCore(
            SmtpOptionsFactory.Build(_options, isDevelopment, certificateFactory),
            serviceProvider);

        _server.SessionCreated += OnSessionCreated;
        _server.SessionCompleted += OnSessionEnded;
        _server.SessionCancelled += OnSessionEnded;
        _server.SessionFaulted += (_, e) =>
        {
            OnSessionEnded(null, e);
            logger.LogWarning(e.Exception, "SMTP session faulted");
        };

        logger.LogInformation(
            "mail sink listening on {Address}; {Transport}; auth: {Auth}; storing .eml in {Destination}",
            _options.ListenAddress,
            DescribeTransport(_options, isDevelopment),
            DescribeAuthentication(_options),
            writer.Destination);

        if (SmtpOptionsFactory.IsPlainText(_options, isDevelopment))
        {
            if (isDevelopment)
            {
                logger.LogWarning(
                    "This sink is running in the Development environment: the connection is not " +
                    "encrypted{Unauthenticated}. Do not expose it beyond your own machine.",
                    _options.HasCredentials ? string.Empty : " and mail is accepted from anyone");
            }
            else
            {
                // A deployed sink with TLS switched off. Loud, and repeated on every start,
                // because the whole point of the setting is to be temporary: the credentials and
                // the mail are on the wire in clear text for anyone on the path to read.
                logger.LogWarning(
                    "TLS IS DISABLED. MailSink:TlsMode is None and this is the {Environment} " +
                    "environment, so this sink accepts mail over an unencrypted connection: the " +
                    "password every sender authenticates with, and the mail itself, cross the " +
                    "network in clear text and can be read or altered in transit. This is a " +
                    "diagnostic setting. Set TlsMode back to StartTls or Implicit as soon as the " +
                    "test is done, and treat any credential used against it as compromised.",
                    environment.EnvironmentName);
            }
        }

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
                $"Could not bind {DescribeTransport(_options, isDevelopment)} as this user. Ports " +
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
    private static string DescribeTransport(MailSinkOptions options, bool isDevelopment)
    {
        var port = SmtpOptionsFactory.ResolvePort(options, isDevelopment);

        if (SmtpOptionsFactory.IsPlainText(options, isDevelopment))
        {
            return $"plain text on port {port}, no TLS";
        }

        var mode = options.TlsMode == SmtpTlsMode.Implicit ? "implicit TLS" : "STARTTLS";
        var floor = options.Tls.MinimumProtocol == TlsProtocolFloor.Tls13 ? "TLS 1.3" : "TLS 1.2+";
        return $"{mode} on port {port} ({floor})";
    }

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
