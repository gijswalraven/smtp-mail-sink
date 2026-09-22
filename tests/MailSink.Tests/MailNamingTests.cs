using System.Globalization;
using System.Text;
using MailSink;

namespace MailSink.Tests;

public class MailNamingTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 21, 10, 23, 57, 243, TimeSpan.FromHours(2));

    [Fact]
    public void Build_uses_date_folder_time_recipient_and_subject()
    {
        var name = MailNaming.Build(At, "app@example.test", ["gijs@example.test"], "Order confirmed", groupByDate: true);

        Assert.Equal("2026-09-21", name.Folder);
        Assert.Equal("102357-243_gijs@example.test_Order-confirmed.eml", name.FileName);
        Assert.Equal("2026-09-21/102357-243_gijs@example.test_Order-confirmed.eml", name.RelativePath);
    }

    [Fact]
    public void Build_omits_the_folder_when_grouping_is_off()
    {
        var name = MailNaming.Build(At, "app@example.test", ["gijs@example.test"], "Hello", groupByDate: false);

        Assert.Equal(string.Empty, name.Folder);
        Assert.Equal("102357-243_gijs@example.test_Hello.eml", name.RelativePath);
    }

    [Fact]
    public void Build_falls_back_to_the_sender_when_there_are_no_recipients()
    {
        var name = MailNaming.Build(At, "app@example.test", [], "Hello", groupByDate: true);

        Assert.Equal("102357-243_app@example.test_Hello.eml", name.FileName);
    }

    [Fact]
    public void Build_uses_only_the_first_recipient()
    {
        var name = MailNaming.Build(At, "app@example.test", ["a@example.test", "b@example.test"], null, groupByDate: true);

        Assert.Equal("102357-243_a@example.test.eml", name.FileName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_leaves_out_a_missing_subject(string? subject)
    {
        var name = MailNaming.Build(At, "app@example.test", ["gijs@example.test"], subject, groupByDate: true);

        Assert.Equal("102357-243_gijs@example.test.eml", name.FileName);
    }

    [Fact]
    public void Build_keeps_non_ascii_characters()
    {
        var name = MailNaming.Build(At, "a@b.test", ["ops@example.test"], "café serveur", groupByDate: true);

        Assert.Equal("102357-243_ops@example.test_café-serveur.eml", name.FileName);
    }

    [Theory]
    [InlineData("a/b", "a-b")]
    [InlineData("a\\b", "a-b")]
    [InlineData("a:b", "a-b")]
    [InlineData("a*b?c", "a-b-c")]
    [InlineData("a<b>c", "a-b-c")]
    [InlineData("a|b\"c", "a-b-c")]
    [InlineData("a   b", "a-b")]
    [InlineData("  padded  ", "padded")]
    [InlineData("...dots...", "dots")]
    public void Build_replaces_characters_that_are_illegal_in_a_file_name(string subject, string expected)
    {
        var name = MailNaming.Build(At, "a@b.test", ["to@example.test"], subject, groupByDate: true);

        Assert.Equal($"102357-243_to@example.test_{expected}.eml", name.FileName);
    }

    [Theory]
    // All of these reached the file name -- and the log line -- on Linux, where
    // Path.GetInvalidFileNameChars() reports only NUL and '/'.
    [InlineData("clear\u001b[2J screen", "clear-[2J-screen")]   // ANSI escape
    [InlineData("bell\u0007ring", "bell-ring")]                 // BEL
    [InlineData("spoof‮gnp.exe", "spoof-gnp.exe")]         // right-to-left override
    [InlineData("zero​width", "zero-width")]               // zero-width space
    public void Build_strips_control_and_format_characters(string subject, string expected)
    {
        var name = MailNaming.Build(At, "a@b.test", ["to@example.test"], subject, groupByDate: true);

        Assert.Equal($"102357-243_to@example.test_{expected}.eml", name.FileName);
    }

    [Fact]
    public void Build_caps_the_subject_length()
    {
        var subject = new string('x', 200);

        var name = MailNaming.Build(At, "a@b.test", ["to@example.test"], subject, groupByDate: true);

        var slug = name.FileName.Split('_')[^1].Replace(".eml", string.Empty);
        Assert.Equal(MailNaming.MaxSlugBytes, slug.Length);
    }

    [Fact]
    public void Build_keeps_a_multi_byte_name_inside_the_filesystem_limit()
    {
        // Four bytes per rune in both slugs. Budgeting in characters instead of bytes pushed the
        // name past the 255-byte Linux limit, and then every attempt to write it failed.
        var wide = string.Concat(Enumerable.Repeat("\U0001F4E7", 200));

        var name = MailNaming.Build(At, "a@b.test", [wide], wide, groupByDate: true);

        Assert.True(
            Encoding.UTF8.GetByteCount(name.WithAttempt(1000).FileName) <= 255,
            $"{name.FileName} is {Encoding.UTF8.GetByteCount(name.FileName)} bytes");
    }

    [Fact]
    public void Build_does_not_split_a_surrogate_pair_at_the_budget()
    {
        // 15 four-byte runes fill MaxSlugBytes exactly; the next one must be dropped whole.
        var subject = string.Concat(Enumerable.Repeat("\U0001F4E7", 16));

        var name = MailNaming.Build(At, "a@b.test", ["ops@example.test"], subject, groupByDate: true);

        // Re-reading the name as runes turns any unpaired surrogate into U+FFFD, so the absence
        // of one is what proves the budget did not cut a pair in half.
        Assert.All(name.FileName.EnumerateRunes(), rune => Assert.NotEqual(0xFFFD, rune.Value));
    }

    [Fact]
    public void Build_formats_the_date_and_time_the_same_in_every_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // th-TH uses the Buddhist calendar, which turned the folder into 2569-09-21.
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");

            var name = MailNaming.Build(At, "a@b.test", ["to@example.test"], "Hello", groupByDate: true);

            Assert.Equal("2026-09-21", name.Folder);
            Assert.StartsWith("102357-243_", name.FileName);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Build_produces_a_name_with_no_path_separators()
    {
        var name = MailNaming.Build(At, "a@b.test", ["to/../../etc@example.test"], "../../escape", groupByDate: true);

        Assert.DoesNotContain('/', name.FileName);
        Assert.DoesNotContain('\\', name.FileName);
        Assert.Equal(name.FileName, Path.GetFileName(name.FileName));
    }

    [Fact]
    public void WithAttempt_adds_a_suffix_and_keeps_the_extension()
    {
        var name = MailNaming.Build(At, "a@b.test", ["to@example.test"], "Hello", groupByDate: true);

        var second = name.WithAttempt(2);

        Assert.Equal("102357-243_to@example.test_Hello_2.eml", second.FileName);
        Assert.Equal("2026-09-21", second.Folder);
    }
}
