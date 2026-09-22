using System.Buffers;
using System.Globalization;
using System.Text;

namespace MailSink;

/// <summary>
/// Where a captured message is stored: up to two folder segments and a file name. The segments
/// are kept apart rather than pre-joined so the writer decides what separator the running
/// filesystem wants, and neither of them can smuggle one in.
/// </summary>
/// <param name="Account">Folder of the account that delivered it; empty when there is none.</param>
/// <param name="Date">yyyy-MM-dd folder; empty when MailSink:GroupByDate is off.</param>
public readonly record struct MailName(string Account, string Date, string FileName)
{
    /// <summary>The segments that are actually present, outermost first.</summary>
    public IEnumerable<string> Folders
    {
        get
        {
            if (!string.IsNullOrEmpty(Account))
            {
                yield return Account;
            }

            if (!string.IsNullOrEmpty(Date))
            {
                yield return Date;
            }
        }
    }

    public string RelativePath => string.Join('/', Folders.Append(FileName));

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
        string? accountFolder,
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
            accountFolder ?? string.Empty,
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

    /// <summary>
    /// Longest an account folder may be, in UTF-8 bytes. Well inside every limit we target; the
    /// point is only that a folder cannot eat the budget a file name needs.
    /// </summary>
    public const int MaxFolderBytes = 64;

    /// <summary>
    /// Names Windows refuses whatever extension follows them, so a folder called "con" would be
    /// a startup error on Linux's side of a deployment and an unwritable path on Windows.
    /// </summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Why <paramref name="folder"/> cannot be a directory under the mail directory, or null when
    /// it can. An empty folder is valid and means the root.
    /// </summary>
    /// <remarks>
    /// Rejects rather than slugs, unlike everything else here. A subject comes off the wire and
    /// has to be accepted in whatever shape it arrives; a folder comes from configuration, where
    /// silently storing mail somewhere other than the name an operator wrote would be worse than
    /// refusing to start. This is also the only path component the sink does not generate itself,
    /// so it is the only one that could contain a separator at all.
    /// </remarks>
    public static string? DescribeInvalidFolder(string folder)
    {
        const string Prefix = "cannot be used as a folder name: it ";

        if (folder.Length == 0)
        {
            return null;
        }

        if (folder.AsSpan().IndexOfAny('/', '\\') >= 0)
        {
            return Prefix + "contains a path separator, and an account writes to one folder " +
                "directly under the mail directory.";
        }

        if (folder is "." or "..")
        {
            return Prefix + "names a relative path rather than a folder.";
        }

        foreach (var rune in folder.EnumerateRunes())
        {
            if (IsUnsafe(rune))
            {
                return Prefix + $"contains '{rune}', which is reserved, a control character, or a " +
                    "format character.";
            }
        }

        // Windows strips both, so the folder that ends up on disk is not the one that was
        // configured -- and then no longer matches what an operator greps for.
        if (char.IsWhiteSpace(folder[0]) || char.IsWhiteSpace(folder[^1]) || folder[^1] == '.')
        {
            return Prefix + "starts or ends with a space or a dot, which Windows strips.";
        }

        if (ReservedDeviceNames.Contains(folder.Split('.')[0]))
        {
            return Prefix + "is a reserved device name on Windows.";
        }

        return Encoding.UTF8.GetByteCount(folder) > MaxFolderBytes
            ? Prefix + $"is longer than the {MaxFolderBytes}-byte limit on a folder name."
            : null;
    }
}
