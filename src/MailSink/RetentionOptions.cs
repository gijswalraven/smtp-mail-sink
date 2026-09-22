namespace MailSink;

/// <summary>
/// How long captured mail is kept, bound from the "MailSink:Retention" configuration section.
/// </summary>
/// <remarks>
/// Off unless an age is configured. A sink that started deleting mail because it was upgraded
/// would be worse than one that fills a share: the mail is evidence of what an application sent,
/// and nothing else has a copy of it.
/// </remarks>
public sealed class RetentionOptions
{
    /// <summary>
    /// How long a .eml file is kept, measured from when it was last written. Zero, the default,
    /// keeps everything forever.
    /// </summary>
    public TimeSpan MaxAge { get; set; }

    /// <summary>
    /// How often the mail directory is swept. A sweep also runs at startup, so a sink that was
    /// down over the weekend does not wait an hour before clearing what expired while it was off.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>True once an age is configured, and so when the sweeper runs at all.</summary>
    public bool IsEnabled => MaxAge > TimeSpan.Zero;
}
