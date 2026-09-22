using MailSink;

namespace MailSink.Tests;

/// <summary>
/// What can be asserted about the blob writer without a storage account: the configuration it will
/// and will not accept, which containers it decides to use, and the blob names it derives from a
/// <see cref="MailName"/>. The upload and the sweep are thin calls onto the SDK and are covered by
/// using them, not by a mock of the REST API.
/// </summary>
public class BlobMailWriterTests
{
    [Theory]
    [InlineData("https://mailsink.blob.core.windows.net")]
    // A trailing slash is what a copy out of the portal tends to carry.
    [InlineData("https://mailsink.blob.core.windows.net/")]
    public void Accepts_an_account_endpoint(string value) =>
        Assert.Equal(
            "https://mailsink.blob.core.windows.net/",
            BlobMailWriter.ParseServiceUri(value).ToString());

    [Theory]
    // Not a URI at all.
    [InlineData("mailsink")]
    // A container, which is now named per account instead.
    [InlineData("https://mailsink.blob.core.windows.net/mail")]
    // Unencrypted, which would put captured mail on the wire in the clear.
    [InlineData("http://mailsink.blob.core.windows.net")]
    public void Refuses_anything_that_is_not_one(string value) =>
        Assert.Throws<InvalidOperationException>(() => BlobMailWriter.ParseServiceUri(value));

    [Fact]
    public void Refuses_a_sas_token()
    {
        // The whole point of this writer is that no bearer credential for the storage account
        // exists. One arriving in configuration anyway is worth refusing to start over.
        var ex = Assert.Throws<InvalidOperationException>(() => BlobMailWriter.ParseServiceUri(
            "https://mailsink.blob.core.windows.net?sv=2024-11-04&sig=redacted"));

        Assert.Contains("managed identity", ex.Message);
    }

    [Fact]
    public void Names_a_blob_after_the_date_only_because_the_container_is_the_account()
    {
        // The account is the container, so it is not repeated in the blob name; the date stays a
        // prefix, which the portal, Storage Explorer and azcopy all show as a folder.
        var name = new MailName("orders", "2026-09-21", "100137-883_gijs@example.test_Hello.eml");

        Assert.Equal("2026-09-21/100137-883_gijs@example.test_Hello.eml", name.PathWithinAccount);
        Assert.Equal("2026-09-21/100137-883_gijs@example.test_Hello_2.eml",
            name.WithAttempt(2).PathWithinAccount);
    }

    [Fact]
    public void Names_a_blob_without_a_prefix_when_dates_are_off()
    {
        var name = new MailName("orders", string.Empty, "m.eml");

        Assert.Equal("m.eml", name.PathWithinAccount);
    }

    [Fact]
    public void Uses_one_container_per_account_plus_one_for_mail_with_no_account()
    {
        var options = new MailSinkOptions
        {
            Blob = { ServiceUri = "https://mailsink.blob.core.windows.net", Container = "mail" },
            Accounts =
            {
                ["orders"] = new MailAccount { Username = "o", Password = "p" },
                ["crm"] = new MailAccount { Username = "c", Password = "p", Folder = "crm-mail" },
            },
        };

        Assert.Equal(["mail", "orders", "crm-mail"], BlobMailWriter.ResolveContainers(options));
    }

    [Fact]
    public void Counts_a_shared_folder_once()
    {
        // Two accounts may deliberately share a destination -- an application and its test
        // harness, say. Retention would otherwise sweep that container twice per pass.
        var options = new MailSinkOptions
        {
            Blob = { ServiceUri = "https://mailsink.blob.core.windows.net" },
            Accounts =
            {
                ["app"] = new MailAccount { Username = "a", Password = "p", Folder = "shared" },
                ["harness"] = new MailAccount { Username = "h", Password = "p", Folder = "shared" },
            },
        };

        Assert.Equal(["mail", "shared"], BlobMailWriter.ResolveContainers(options));
    }

    [Theory]
    // The folder rules allow all of these; a container name does not.
    [InlineData("Orders", "lower-case")]
    [InlineData("hr", "between 3 and 63 characters")]
    [InlineData("orders--eu", "two hyphens in a row")]
    [InlineData("orders-", "starts or ends with a hyphen")]
    [InlineData("orders.eu", "lower-case")]
    public void Refuses_a_folder_that_cannot_be_a_container(string folder, string because)
    {
        // A folder is a container once mail goes to blob storage, so a name a filesystem would take
        // and Azure would not has to fail at startup rather than on the first message.
        var options = new MailSinkOptions
        {
            Blob = { ServiceUri = "https://mailsink.blob.core.windows.net" },
            Accounts = { ["app"] = new MailAccount { Username = "a", Password = "p", Folder = folder } },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains(because, ex.Message);
    }

    [Fact]
    public void Accepts_the_same_folder_when_mail_goes_to_the_filesystem()
    {
        // The rules are the destination's, not the sink's: "Orders" is a perfectly good folder.
        var options = new MailSinkOptions
        {
            Accounts = { ["app"] = new MailAccount { Username = "a", Password = "p", Folder = "Orders" } },
        };

        options.Validate(isDevelopment: true);
    }

    [Fact]
    public void Refuses_a_default_container_that_is_not_a_container_name()
    {
        var options = new MailSinkOptions
        {
            Blob = { ServiceUri = "https://mailsink.blob.core.windows.net", Container = "Mail" },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: true));

        Assert.Contains("Blob:Container", ex.Message);
    }
}
