using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace MailSink;

/// <summary>
/// Adds <c>appsettings.local.json</c>: an optional, gitignored overlay for settings that should
/// not be in source control -- <c>MailSink:Password</c> above all.
/// </summary>
public static class LocalSettingsConfigurationExtensions
{
    public const string FileName = "appsettings.local.json";

    /// <summary>
    /// Slots the local file in directly after the other appsettings files, so it overrides them
    /// but still loses to user secrets, environment variables and the command line.
    /// </summary>
    public static IConfigurationBuilder AddLocalSettings(this IConfigurationBuilder builder)
    {
        // Appending instead would place the file last, letting a forgotten local file silently
        // beat MailSink__Password=... in a container -- the one place the override has to win.
        builder.Sources.Insert(
            IndexAfterLastJsonSource(builder.Sources),
            new JsonConfigurationSource
            {
                Path = FileName,
                Optional = true,
                ReloadOnChange = true,
            });

        return builder;
    }

    /// <summary>
    /// Where the local file belongs: after the last JSON source, or first if there is none.
    /// </summary>
    internal static int IndexAfterLastJsonSource(IList<IConfigurationSource> sources)
    {
        for (var i = sources.Count - 1; i >= 0; i--)
        {
            if (sources[i] is JsonConfigurationSource)
            {
                return i + 1;
            }
        }

        return 0;
    }
}
