namespace MailSink;

/// <summary>Configuration for the sink, bound from the "MailSink" configuration section.</summary>
public sealed class MailSinkOptions
{
    public const string SectionName = "MailSink";

    /// <summary>Folder the .eml files are written to. Relative paths are resolved against the content root.</summary>
    public string MailDirectory { get; set; } = "mail";

    /// <summary>Name the server reports in its SMTP greeting.</summary>
    public string ServerName { get; set; } = "mail-sink";

    /// <summary>
    /// Ports to listen on; falls back to <see cref="DefaultPort"/> when empty. 1025 is the
    /// conventional sink port; 25 is privileged on Linux. Left empty on purpose: the configuration
    /// binder appends to array defaults rather than replacing them, so a default here would be
    /// added to whatever the config file specifies.
    /// </summary>
    public int[] Ports { get; set; } = [];

    public const int DefaultPort = 1025;

    /// <summary>Address to bind to. 0.0.0.0 accepts from other machines and containers.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>Largest accepted message in bytes; anything bigger is rejected with 552.</summary>
    public int MaxMessageSize { get; set; } = 25 * 1024 * 1024;

    /// <summary>
    /// Advertise AUTH and accept any username/password. A sink has no accounts to protect; this
    /// only exists so apps that insist on authenticating can connect unchanged. Turning it off
    /// stops AUTH being offered and registers no authenticator at all, so a client that insists on
    /// authenticating is refused. Mail is still accepted without AUTH either way -- this is a sink,
    /// authentication is not a security boundary here.
    /// <para>
    /// Ignored when <see cref="Username"/> is set: an explicit credential pair is the more
    /// specific instruction, so it wins rather than being silently cancelled by this flag.
    /// </para>
    /// </summary>
    public bool AllowAnyCredentials { get; set; } = true;

    /// <summary>
    /// Username AUTH must present. Leave empty to keep the accept-anything behaviour. Set it
    /// together with <see cref="Password"/> to make the sink reject every other credential pair,
    /// which is what lets a test assert that an application sends the credentials it was
    /// configured with.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password that goes with <see cref="Username"/>. Required once a username is set.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Refuse MAIL FROM until the session has authenticated. Off by default, so a sender that
    /// never authenticates keeps working even once credentials are configured; turn it on to
    /// prove that an application really does authenticate.
    /// </summary>
    public bool RequireAuthentication { get; set; }

    /// <summary>True when a specific credential pair is configured rather than accept-anything.</summary>
    public bool HasFixedCredentials => !string.IsNullOrEmpty(Username);

    /// <summary>
    /// True when the sink has some way to authenticate a session, and so can advertise AUTH.
    /// </summary>
    public bool OffersAuthentication => HasFixedCredentials || AllowAnyCredentials;

    /// <summary>
    /// Rejects credential settings that cannot do what they say, rather than letting the sink
    /// start in a state where every message is refused or a username is quietly unenforced.
    /// </summary>
    public void ValidateCredentials()
    {
        if (HasFixedCredentials && string.IsNullOrEmpty(Password))
        {
            throw new InvalidOperationException(
                "MailSink:Password is required when MailSink:Username is set; a username on its own " +
                "would accept any password.");
        }

        if (!HasFixedCredentials && !string.IsNullOrEmpty(Password))
        {
            throw new InvalidOperationException(
                "MailSink:Password is set without MailSink:Username, so nothing would enforce it.");
        }

        if (RequireAuthentication && !OffersAuthentication)
        {
            throw new InvalidOperationException(
                "MailSink:RequireAuthentication is on but nothing can authenticate a session: set " +
                "MailSink:Username and MailSink:Password, or leave MailSink:AllowAnyCredentials on.");
        }
    }

    /// <summary>
    /// Connections served at once; further connections are dropped until one finishes. Each
    /// in-flight message is held in memory up to <see cref="MaxMessageSize"/>, so this is what
    /// bounds the sink's memory use. 0 disables the limit.
    /// </summary>
    public int MaxConcurrentSessions { get; set; } = 64;

    /// <summary>Put each day's mail in its own yyyy-MM-dd subfolder.</summary>
    public bool GroupByDate { get; set; } = true;
}
