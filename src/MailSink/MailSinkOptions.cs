namespace MailSink;

/// <summary>Configuration for the sink, bound from the "MailSink" configuration section.</summary>
public sealed class MailSinkOptions
{
    public const string SectionName = "MailSink";

    /// <summary>Folder the .eml files are written to. Relative paths are resolved against the content root.</summary>
    public string MailDirectory { get; set; } = "mail";

    /// <summary>
    /// <see cref="MailDirectory"/> as an absolute path. The content root rather than the working
    /// directory, because a Windows service starts in system32 and a relative folder would then be
    /// created there.
    /// </summary>
    public string ResolveMailDirectory(string contentRootPath) =>
        Path.GetFullPath(MailDirectory, contentRootPath);

    /// <summary>Name the server reports in its SMTP greeting, and the name a certificate should cover.</summary>
    public string ServerName { get; set; } = "mail-sink";

    /// <summary>
    /// Plain-text ports. <b>Development only</b> -- configuring one anywhere else fails at
    /// startup, so a loose port cannot be left open in production by accident.
    /// </summary>
    /// <remarks>
    /// This and the two TLS port lists are all left empty on purpose, with their real defaults in
    /// <see cref="SmtpOptionsFactory"/>: the configuration binder appends to array defaults rather
    /// than replacing them, so a default here would be added to whatever the config file specifies.
    /// </remarks>
    public int[] Ports { get; set; } = [];

    /// <summary>Ports that start in plain text and require STARTTLS before AUTH.</summary>
    public int[] StartTlsPorts { get; set; } = [];

    /// <summary>Ports that are TLS from the first byte.</summary>
    public int[] ImplicitTlsPorts { get; set; } = [];

    /// <summary>1025 is the conventional sink port; 25 is privileged on Linux.</summary>
    public const int DefaultPort = 1025;

    /// <summary>587, the submission port, is where STARTTLS belongs.</summary>
    public const int DefaultStartTlsPort = 587;

    /// <summary>465, the conventional implicit-TLS submission port.</summary>
    public const int DefaultImplicitTlsPort = 465;

    /// <summary>Address to bind to. 0.0.0.0 accepts from other machines and containers.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>Largest accepted message in bytes; anything bigger is rejected with 552.</summary>
    public int MaxMessageSize { get; set; } = 25 * 1024 * 1024;

    /// <summary>
    /// Username AUTH must present. Required outside Development. In Development it is optional:
    /// leave it empty and the sink advertises no AUTH and accepts mail from anyone, which is the
    /// point of running one locally.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password that goes with <see cref="Username"/>. Required once a username is set.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>TLS settings. Required outside Development.</summary>
    public TlsOptions Tls { get; set; } = new();

    /// <summary>How long captured mail is kept. Off unless an age is configured.</summary>
    public RetentionOptions Retention { get; set; } = new();

    /// <summary>True when a credential pair is configured, and so when AUTH is advertised.</summary>
    public bool HasCredentials => !string.IsNullOrEmpty(Username);

    /// <summary>
    /// Connections served at once; further connections are dropped until one finishes. Each
    /// in-flight message is held in memory up to <see cref="MaxMessageSize"/>, so this is what
    /// bounds the sink's memory use. 0 disables the limit.
    /// </summary>
    public int MaxConcurrentSessions { get; set; } = 64;

    /// <summary>
    /// Connections served at once <i>from a single client address</i>. Without this, one host can
    /// take the whole <see cref="MaxConcurrentSessions"/> budget and starve every other sender.
    /// 0 disables the limit.
    /// </summary>
    public int MaxSessionsPerClient { get; set; } = 8;

    /// <summary>Failed AUTH attempts a session may make before it is dropped.</summary>
    public int MaxAuthenticationAttempts { get; set; } = 3;

    /// <summary>How long a single session may stay open. Bounds a sender that connects and stalls.</summary>
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long the server waits for the next command before giving up on a session.</summary>
    public TimeSpan CommandWaitTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Port for the HTTP health endpoint an orchestrator probes; 0 disables it. Separate from the
    /// SMTP ports and never published, so it is reachable from the host running the probe and not
    /// from a sender. In Development a port already in use is a warning; anywhere else it stops
    /// the host, because a deployment whose probe never answers is restarted forever.
    /// </summary>
    public int HealthPort { get; set; } = 8080;

    /// <summary>Put each day's mail in its own yyyy-MM-dd subfolder.</summary>
    public bool GroupByDate { get; set; } = true;

    /// <summary>
    /// Rejects settings that cannot do what they say, rather than letting the sink start in a
    /// state where a username is quietly unenforced or a production deployment silently listens
    /// in plain text.
    /// </summary>
    /// <param name="isDevelopment">
    /// Whether the host is running in the Development environment. The relaxed rules exist so a
    /// developer can still run <c>dotnet run</c> with no credentials and no certificate; every
    /// other environment gets the strict set.
    /// </param>
    public void Validate(bool isDevelopment)
    {
        if (HasCredentials && string.IsNullOrEmpty(Password))
        {
            throw new InvalidOperationException(
                "MailSink:Password is required when MailSink:Username is set; a username on its own " +
                "would accept any password.");
        }

        if (!HasCredentials && !string.IsNullOrEmpty(Password))
        {
            throw new InvalidOperationException(
                "MailSink:Password is set without MailSink:Username, so nothing would enforce it.");
        }

        ValidateRetention();

        if (isDevelopment)
        {
            return;
        }

        if (!HasCredentials)
        {
            throw new InvalidOperationException(
                "MailSink:Username and MailSink:Password are required outside the Development " +
                "environment. The sink does not accept unauthenticated mail in a deployed " +
                "environment; see SECURITY.md.");
        }

        if (!Tls.IsConfigured)
        {
            throw new InvalidOperationException(
                "MailSink:Tls:KeyVaultCertificateUri is required outside the Development " +
                "environment. The sink does not accept mail over an unencrypted connection in a " +
                "deployed environment; see SECURITY.md.");
        }

        if (Ports.Length > 0)
        {
            throw new InvalidOperationException(
                "MailSink:Ports configures a plain-text listener and is only allowed in the " +
                "Development environment. Use MailSink:StartTlsPorts or MailSink:ImplicitTlsPorts.");
        }
    }

    /// <summary>
    /// Keeps retention from being configured into something that does nothing or spins. Deleting
    /// mail is the one thing here that cannot be undone, so a value that does not say what it
    /// looks like it says is worth refusing to start over.
    /// </summary>
    private void ValidateRetention()
    {
        if (Retention.MaxAge < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Retention:MaxAge is negative, which would delete every message as " +
                "soon as it arrived. Use 0 to keep mail forever.");
        }

        if (Retention.IsEnabled && Retention.SweepInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Retention:SweepInterval has to be positive; an interval of zero " +
                "would sweep the mail directory in a loop.");
        }
    }
}
