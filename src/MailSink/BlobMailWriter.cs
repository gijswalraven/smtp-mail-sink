using System.Collections.Concurrent;
using System.Net;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace MailSink;

/// <summary>
/// Writes each message to a block blob in Azure Blob Storage, in a container of its own per
/// account, authenticating as the host's own managed identity.
/// </summary>
/// <remarks>
/// <para>
/// No storage account key reaches this path, which is the reason it exists. A key is a bearer
/// credential for the whole account with no user behind it: anyone holding one is indistinguishable
/// from the sink in the storage logs, and revoking it means rotating it for every other holder at
/// the same moment. Reaching the container with an identity instead makes every write attributable,
/// and puts reads behind Entra ID RBAC.
/// </para>
/// <para>
/// A container per account rather than a folder per account, because a container is the smallest
/// thing a role assignment can be scoped to. That is the whole difference between separation the
/// sink arranges -- one client's mail merely not listed among another's -- and separation the
/// storage account enforces: <c>Storage Blob Data Reader</c> on <c>orders</c> grants exactly the
/// orders mail, and no grant at all reads everything. The date folder stays a blob name prefix,
/// which the portal, Storage Explorer and azcopy all show as a folder.
/// </para>
/// </remarks>
public sealed class BlobMailWriter : IMailWriter
{
    /// <summary>Give up rather than spin forever if something keeps taking the names we pick.</summary>
    private const int MaxNameAttempts = 1000;

    /// <summary>What a stored message is served as, so downloading one opens it in a mail client.</summary>
    private const string MessageContentType = "message/rfc822";

    private readonly BlobServiceClient _service;
    private readonly ILogger<BlobMailWriter> _logger;
    private readonly string _defaultContainer;
    private readonly IReadOnlyList<string> _containers;
    private readonly ConcurrentDictionary<string, BlobContainerClient> _clients = new(StringComparer.Ordinal);

    public BlobMailWriter(IOptions<MailSinkOptions> options, ILogger<BlobMailWriter> logger)
    {
        var blob = options.Value.Blob;
        _logger = logger;
        _defaultContainer = blob.Container;
        _containers = ResolveContainers(options.Value);

        // Nothing is called here. The containers are expected to exist -- deploy.ps1 creates one
        // per account -- and a role assignment that has not propagated yet is the normal state of a
        // first deployment, so a check at startup would fail a container group that is about to be
        // fine. A write that cannot be made answers the sender 451, which is the reply that asks it
        // to hold the message and try again.
        _service = new BlobServiceClient(
            ParseServiceUri(blob.ServiceUri),
            SinkAzureCredential.Create(blob.ManagedIdentityClientId));
    }

    public string Destination => $"{_service.Uri} ({string.Join(", ", _containers)})";

    public async Task<string> WriteAsync(MailName name, byte[] raw, CancellationToken cancellationToken)
    {
        var container = Container(name.Account);

        // The blob name is what MailName already produces below the account: sanitised segments
        // joined with "/". There is no equivalent of FileMailWriter's containment check because
        // there is nothing to contain -- the account and container come from configuration, and a
        // message can only ever add a prefix inside the container its own account authenticated to.
        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var blob = container.GetBlobClient(
                (attempt == 1 ? name : name.WithAttempt(attempt)).PathWithinAccount);

            try
            {
                // IfNoneMatch: * is what makes this a create rather than an overwrite. A burst of
                // mail can share the same millisecond and two sessions can race for one name; the
                // loser is refused and takes the next. Asking whether the blob exists first would
                // leave a window in which both callers believe the name is free and one silently
                // replaces the other's message.
                await blob.UploadAsync(
                    new BinaryData(raw),
                    new BlobUploadOptions
                    {
                        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                        HttpHeaders = new BlobHttpHeaders { ContentType = MessageContentType },
                    },
                    cancellationToken);

                return blob.Uri.ToString();
            }
            catch (RequestFailedException ex) when (
                ex.Status is (int)HttpStatusCode.Conflict or (int)HttpStatusCode.PreconditionFailed)
            {
                // The name was taken between our choosing it and the upload. Both statuses are
                // reported for that depending on the service version, and this call carries no
                // other condition, so neither can mean anything else here. Anything else -- no
                // permission, no container -- is a real failure and propagates.
                continue;
            }
        }

        throw new IOException(
            $"Could not find a free blob name for '{name.PathWithinAccount}' in '{container.Uri}'.");
    }

    public async Task<SweepResult> SweepAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var deleted = 0;

        foreach (var container in _containers)
        {
            deleted += await SweepContainerAsync(Container(container), cutoff, cancellationToken);
        }

        // Never any folders: a prefix is part of a blob name, so the last message under a date is
        // the last trace of that date.
        return new SweepResult(deleted, 0);
    }

    private async Task<int> SweepContainerAsync(
        BlobContainerClient container,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var deleted = 0;

        try
        {
            // Deleting while the listing is still being paged through is safe here, unlike on a
            // filesystem: each page is a fresh request against a continuation token rather than a
            // live cursor over the container.
            await foreach (var item in container.GetBlobsAsync(cancellationToken: cancellationToken))
            {
                if (!item.Name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) ||
                    item.Properties.LastModified is not { } written ||
                    written >= cutoff)
                {
                    continue;
                }

                try
                {
                    // IncludeSnapshots, so that if the account ever grows a policy that takes them,
                    // a snapshot cannot make the blob undeletable and leave the sweep stuck on the
                    // same message every hour.
                    var response = await container.GetBlobClient(item.Name)
                        .DeleteIfExistsAsync(
                            DeleteSnapshotsOption.IncludeSnapshots,
                            cancellationToken: cancellationToken);

                    if (response.Value)
                    {
                        deleted++;
                    }
                }
                catch (RequestFailedException ex)
                {
                    _logger.LogWarning(ex, "retention: could not delete {Blob}", item.Name);
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            // A container that was never created holds nothing to expire. That is the ordinary
            // state of the default container in a deployment where every account has its own, so
            // it is not worth a warning every hour.
            _logger.LogDebug("retention: {Container} does not exist, nothing to sweep", container.Uri);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(
                ex, "retention: could not list {Container}, skipping it this sweep", container.Uri);
        }

        return deleted;
    }

    /// <summary>
    /// The client for one container, created once and kept. <paramref name="accountFolder"/> is
    /// empty for mail that belongs to no account, which goes to MailSink:Blob:Container.
    /// </summary>
    private BlobContainerClient Container(string accountFolder) =>
        _clients.GetOrAdd(
            accountFolder.Length == 0 ? _defaultContainer : accountFolder,
            name =>
            {
                // MailSinkOptions.Validate has already refused a folder that cannot be a container.
                // This is the check that has to hold if a name ever reaches the writer without
                // going through that validation -- the counterpart of FileMailWriter's refusal to
                // resolve outside the mail directory.
                if (MailNaming.DescribeInvalidContainerName(name) is { } problem)
                {
                    throw new InvalidOperationException($"Refusing to write to '{name}': it {problem}");
                }

                return _service.GetBlobContainerClient(name);
            });

    /// <summary>
    /// Every container the sink writes to, which is also every container retention sweeps.
    /// </summary>
    /// <remarks>
    /// The default container is included whenever any mail could land in it: the flat
    /// MailSink:Username shorthand and an account with an explicitly empty folder both write there,
    /// and so does an unauthenticated session, which only Development allows. Including one that
    /// was never created costs a listing that 404s and is skipped; leaving one out would be mail
    /// that quietly never expires.
    /// </remarks>
    internal static IReadOnlyList<string> ResolveContainers(MailSinkOptions options)
    {
        var accounts = options.ResolveAccounts();

        return
        [
            options.Blob.Container,
            .. accounts
                .Select(account => account.Folder)
                .Where(folder => folder.Length > 0)
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// The blob endpoint of the storage account, or an explanation of why the configured value is
    /// not one. Rejects rather than repairs: mail stored in an account nobody named is worse than a
    /// sink that refuses to start.
    /// </summary>
    internal static Uri ParseServiceUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"MailSink:Blob:ServiceUri '{value}' is not an absolute https URI.");
        }

        // No path, because the container is named per account rather than here, and no query: a
        // query is a SAS token, a bearer credential sitting in configuration, which is the thing
        // this writer exists to be rid of.
        if (uri.AbsolutePath.Trim('/').Length > 0 || uri.Query.Length > 0)
        {
            throw new InvalidOperationException(
                $"MailSink:Blob:ServiceUri '{value}' should be the account endpoint on its own, " +
                "https://<account>.blob.core.windows.net, with no container and no SAS token: each " +
                "account writes to its own container, and the sink authenticates with a managed " +
                "identity.");
        }

        return new Uri($"{uri.Scheme}://{uri.Authority}");
    }
}
