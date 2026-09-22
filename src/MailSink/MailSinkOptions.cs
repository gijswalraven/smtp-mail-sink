namespace MailSink;

/// <summary>Configuration for the sink, bound from the "MailSink" configuration section.</summary>
public sealed class MailSinkOptions
{
    public const string SectionName = "MailSink";

    /// <summary>
    /// Folder the .eml files are written to. Relative paths are resolved against the content root.
    /// Ignored once <see cref="Blob"/> names a container, which is where a deployed sink writes.
    /// </summary>
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
    /// Shorthand for a single account that writes to the root of the mail directory, which is
    /// the whole configuration a one-client sink needs. Mutually exclusive with
    /// <see cref="Accounts"/>. Required outside Development unless accounts are configured; in
    /// Development both may be left out, and the sink then advertises no AUTH and accepts mail
    /// from anyone, which is the point of running one locally.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password that goes with <see cref="Username"/>. Required once a username is set.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The accounts the sink accepts, keyed by a name that identifies each one in logs and in
    /// configuration. Each writes to its own <see cref="MailAccount.Folder"/>, so several clients
    /// can share a sink without sharing a directory.
    /// </summary>
    /// <remarks>
    /// A dictionary rather than an array: keys are stable, so one account can be overridden by a
    /// single environment variable without knowing its position, and the binder's habit of
    /// appending to array defaults cannot apply.
    /// </remarks>
    public Dictionary<string, MailAccount> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>TLS settings. Required outside Development.</summary>
    public TlsOptions Tls { get; set; } = new();

    /// <summary>
    /// Azure Blob Storage destination. Naming a container sends captured mail there instead of to
    /// <see cref="MailDirectory"/>, authenticated as the host's managed identity.
    /// </summary>
    public BlobOptions Blob { get; set; } = new();

    /// <summary>How long captured mail is kept. Off unless an age is configured.</summary>
    public RetentionOptions Retention { get; set; } = new();

    /// <summary>True when at least one account is configured, and so when AUTH is advertised.</summary>
    public bool HasCredentials => !string.IsNullOrEmpty(Username) || Accounts.Count > 0;

    /// <summary>
    /// Every account the sink accepts, with its folder resolved. Flattens the two ways of
    /// configuring one so nothing downstream has to know which was used.
    /// </summary>
    public IReadOnlyList<ResolvedAccount> ResolveAccounts() =>
        !string.IsNullOrEmpty(Username)
            ? [new ResolvedAccount(Username, Username, Password, string.Empty)]
            : [.. Accounts.Select(entry => new ResolvedAccount(
                entry.Key,
                entry.Value.Username,
                entry.Value.Password,
                entry.Value.FolderOrKey(entry.Key)))];

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
        var hasFlatPair = !string.IsNullOrEmpty(Username);

        if (hasFlatPair && Accounts.Count > 0)
        {
            throw new InvalidOperationException(
                "MailSink:Username and MailSink:Accounts both configure credentials, and there is " +
                "no honest way to honour both. Move the pair into MailSink:Accounts; an account " +
                "with an empty Folder writes exactly where the flat pair does.");
        }

        if (hasFlatPair && string.IsNullOrEmpty(Password))
        {
            throw new InvalidOperationException(
                "MailSink:Password is required when MailSink:Username is set; a username on its own " +
                "would accept any password.");
        }

        if (!hasFlatPair && Accounts.Count == 0 && !string.IsNullOrEmpty(Password))
        {
            throw new InvalidOperationException(
                "MailSink:Password is set without MailSink:Username, so nothing would enforce it.");
        }

        ValidateAccounts();
        ValidateRetention();
        ValidateBlobContainers();

        if (isDevelopment)
        {
            return;
        }

        if (!HasCredentials)
        {
            throw new InvalidOperationException(
                "MailSink:Accounts, or the MailSink:Username and MailSink:Password pair, is " +
                "required outside the Development environment. The sink does not accept " +
                "unauthenticated mail in a deployed environment; see SECURITY.md.");
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

    /// <summary>
    /// Checks that every folder can be a container, once mail goes to blob storage and a folder is
    /// one. Only then: a filesystem takes names Azure will not, and a local run has no reason to
    /// be held to rules that only apply to a deployment.
    /// </summary>
    /// <remarks>
    /// The asymmetry is a footgun worth naming: an account called "Orders" works on a filesystem
    /// and cannot be a container, so the error says which setting and which rule rather than
    /// leaving it to a 400 from the service on the first message. deploy.ps1 holds -Accounts to
    /// the intersection of both sets of rules for the same reason.
    /// </remarks>
    private void ValidateBlobContainers()
    {
        if (!Blob.IsConfigured)
        {
            return;
        }

        if (MailNaming.DescribeInvalidContainerName(Blob.Container) is { } containerProblem)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Blob:Container '{Blob.Container}' {containerProblem}");
        }

        foreach (var account in ResolveAccounts())
        {
            // An empty folder is the account that writes to Blob:Container, checked just above.
            if (account.Folder.Length == 0 ||
                MailNaming.DescribeInvalidContainerName(account.Folder) is not { } problem)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"The folder '{account.Folder}' of account '{account.Key}' {problem} Captured mail " +
                $"goes to a container per account because {SectionName}:Blob:ServiceUri is set, so " +
                "the folder names one.");
        }
    }

    /// <summary>
    /// Checks each account can do what it says and that no two of them collide. Folder names are
    /// the reason this is not optional: they come from configuration rather than from a message,
    /// and they are the first thing the sink puts in a path that it did not generate itself.
    /// </summary>
    private void ValidateAccounts()
    {
        var usernames = new HashSet<string>(StringComparer.Ordinal);
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, account) in Accounts)
        {
            var path = $"{SectionName}:Accounts:{key}";

            if (string.IsNullOrEmpty(account.Username))
            {
                throw new InvalidOperationException($"{path}:Username is required.");
            }

            if (string.IsNullOrEmpty(account.Password))
            {
                throw new InvalidOperationException(
                    $"{path}:Password is required; an account without one would accept any password.");
            }

            // Ordinal, because the authenticator compares byte for byte. Two accounts sharing a
            // username would make the account a session authenticated as a matter of which entry
            // the comparison loop happened to see last.
            if (!usernames.Add(account.Username))
            {
                throw new InvalidOperationException(
                    $"{path}:Username '{account.Username}' is already used by another account. " +
                    "Usernames have to be unique, or there is no telling which account a session is.");
            }

            var folder = account.FolderOrKey(key);

            if (MailNaming.DescribeInvalidFolder(folder) is { } problem)
            {
                var source = account.Folder is null
                    ? $"The account key '{key}', used as its folder because {path}:Folder is not set,"
                    : $"{path}:Folder '{folder}'";

                throw new InvalidOperationException($"{source} {problem}");
            }

            // Two accounts may deliberately share a folder -- an application and its test harness,
            // say, with separate credentials and one destination. Folders that differ only in case
            // are not that: they would be two directories on Linux and one on Windows and Azure
            // Files, so whichever was meant, one of the two platforms gets it wrong.
            if (folder.Length > 0 &&
                folders.TryGetValue(folder, out var other) &&
                !string.Equals(folder, other, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{path}:Folder '{folder}' differs from '{other}' only in case. That is one " +
                    "directory on Windows and Azure Files and two on Linux.");
            }

            folders[folder] = folder;
        }
    }
}
