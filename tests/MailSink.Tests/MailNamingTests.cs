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
    [InlineData("a   b", "a-b")]
    [InlineData("  padded  ", "padded")]
    [InlineData("...dots...", "dots")]
    public void Build_replaces_characters_that_are_illegal_in_a_file_name(string subject, string expected)
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
        Assert.Equal(MailNaming.MaxSlugLength, slug.Length);
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
