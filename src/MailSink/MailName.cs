using System.Buffers;
using System.Globalization;
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
    /// <summary>
    /// Budget for each of the two variable parts, in UTF-8 bytes rather than characters.
    /// </summary>
    /// <remarks>
    /// Bytes, because Linux caps a file name at 255 of them: counting characters let a subject of
    /// 60 emoji (4 bytes each) push the name past the limit, and then every write of that message
    /// failed. The whole name is bounded at 10 (time) + 2x(1 + 60) + 9 ("_1000.eml") = 141 bytes,
    /// which leaves room on every filesystem we target.
    /// </remarks>
    public const int MaxSlugBytes = 60;

    /// <summary>
    /// Characters that must never reach a file name. Fixed rather than taken from
    /// <see cref="Path.GetInvalidFileNameChars"/>, which is evaluated for the running OS: on Linux
    /// it reports only '\0' and '/', so everything else here survived into names that a mapped SMB
    /// share then could not hold.
    /// </summary>
    private static readonly SearchValues<char> ReservedFileNameChars =
        SearchValues.Create(@"<>:""/\|?*");

    public static MailName Build(
        DateTimeOffset receivedAt,
        string envelopeFrom,
        IReadOnlyList<string> envelopeTo,
        string? subject,
        bool groupByDate)
    {
        // Invariant throughout: the current culture decides the calendar and the digit shapes, so
        // under, say, th-TH these would be Buddhist-era years and under ar-SA non-ASCII digits --
        // names that no longer sort by time and no longer match what the docs promise.
        var name = new StringBuilder(receivedAt.ToString("HHmmss-fff", CultureInfo.InvariantCulture));
        Append(name, envelopeTo.Count > 0 ? envelopeTo[0] : envelopeFrom);
        Append(name, subject);
        name.Append(".eml");

        return new MailName(
            groupByDate ? receivedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty,
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

        var builder = new StringBuilder(MaxSlugBytes);
        var bytes = 0;

        // Hoisted out of the loop: a stackalloc per iteration would grow the frame per rune.
        Span<char> encoded = stackalloc char[2];

        // Runes, not chars: breaking at the budget mid-surrogate-pair used to leave a lone
        // surrogate in the name. EnumerateRunes also folds any unpaired surrogate already in the
        // input into U+FFFD, so malformed subjects cannot produce an unencodable name.
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || IsUnsafe(rune))
            {
                if (builder.Length > 0 && builder[^1] != '-' && bytes < MaxSlugBytes)
                {
                    builder.Append('-');
                    bytes++;
                }

                continue;
            }

            var width = rune.Utf8SequenceLength;
            if (bytes + width > MaxSlugBytes)
            {
                break;
            }

            builder.Append(encoded[..rune.EncodeToUtf16(encoded)]);
            bytes += width;
        }

        return builder.ToString().Trim('-', '.');
    }

    /// <summary>
    /// Whether a rune has no business being in a file name. Beyond the reserved characters, this
    /// drops control and format characters: an ESC in a subject otherwise reached both the file
    /// name and the log line, where a terminal would run it as an escape sequence, and U+202E
    /// (right-to-left override) reverses how the rest of a name is displayed.
    /// </summary>
    private static bool IsUnsafe(Rune rune) =>
        (rune.IsBmp && ReservedFileNameChars.Contains((char)rune.Value)) ||
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format;
}
