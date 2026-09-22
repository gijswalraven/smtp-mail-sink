namespace MailSink;

/// <summary>
/// Whether the sink is actually serving. Written by <see cref="SmtpListenerService"/> and read by
/// <see cref="HealthEndpointService"/>, so an orchestrator can tell a process that is running from
/// one that is running but no longer listening.
/// </summary>
public sealed class SinkHealth
{
    private volatile bool _isListening;

    /// <summary>True between the listener starting and the server loop ending for any reason.</summary>
    public bool IsListening => _isListening;

    public void MarkListening() => _isListening = true;

    public void MarkStopped() => _isListening = false;
}
