using System.Buffers;
using System.Text;

namespace MailSink;

/// <summary>Where a captured message is stored: a folder (possibly empty) and a file name.</summary>
public readonly record struct MailName(string Folder, string FileName)
{
    public string RelativePath =>
        string.IsNullOrEmpty(Folder) ? FileName : $"{Folder}/{FileName}";

    /// <summary>Same name with a numeric suffix, used when the first choice is already taken.</summary>
    public MailName WithAttempt(int attempt) =>
        this with { FileName = $"{Path.GetFileNameWithoutExtension(FileName)}_{attempt}.eml" };
}

/// <summary>
/// Builds storage names that sort by time and stay readable:
/// <c>2026-09-21/102357-243_gijs@example.test_Azure-sink-test.eml</c>
/// </summary>
public static class MailNaming
{
    public const int MaxSlugLength = 60;

    private static readonly SearchValues<char> InvalidFileNameChars =
        // Linux only reports '\0' and '/' as invalid, so add the Windows separators explicitly:
        // names must be portable between the container and a mapped SMB share.
        SearchValues.Create(new string(Path.GetInvalidFileNameChars()) + @"\/:");

    public static MailName Build(
        DateTimeOffset receivedAt,
        string envelopeFrom,
        IReadOnlyList<string> envelopeTo,
        string? subject,
        bool groupByDate)
    {
        var name = new StringBuilder(receivedAt.ToString("HHmmss-fff"));
        Append(name, envelopeTo.Count > 0 ? envelopeTo[0] : envelopeFrom);
        Append(name, subject);
        name.Append(".eml");

        return new MailName(
            groupByDate ? receivedAt.ToString("yyyy-MM-dd") : string.Empty,
            name.ToString());

        static void Append(StringBuilder builder, string? part)
        {
            var slug = Slug(part);
            if (slug.Length > 0)
            {
                builder.Append('_').Append(slug);
            }
        }
    }

    private static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (builder.Length >= MaxSlugLength)
            {
                break;
            }

            if (char.IsWhiteSpace(c) || InvalidFileNameChars.Contains(c))
            {
                if (builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim('-', '.');
    }
}
