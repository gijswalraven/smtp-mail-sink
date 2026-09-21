using MailSink;
using Microsoft.Extensions.Options;

namespace MailSink.Tests;

/// <summary>Keeps written messages in memory so capture behaviour can be asserted without IO.</summary>
public sealed class InMemoryMailWriter : IMailWriter
{
    private readonly List<(MailName Name, byte[] Raw)> _written = [];

    public string Destination => "in-memory";

    public IReadOnlyList<(MailName Name, byte[] Raw)> Written => _written;

    /// <summary>When set, WriteAsync throws this instead of storing, to exercise the failure path.</summary>
    public Exception? ThrowOnWrite { get; set; }

    public Task<string> WriteAsync(MailName name, byte[] raw, CancellationToken cancellationToken)
    {
        if (ThrowOnWrite is not null)
        {
            throw ThrowOnWrite;
        }

        _written.Add((name, raw));
        return Task.FromResult($"in-memory/{name.RelativePath}");
    }
}

public sealed class StubMetadataReader(string? subject, bool parsed = true) : IMessageMetadataReader
{
    public MessageMetadata Read(byte[] raw) => new(subject, parsed);
}

public static class TestOptions
{
    public static IOptions<MailSinkOptions> For(Action<MailSinkOptions>? configure = null)
    {
        var options = new MailSinkOptions();
        configure?.Invoke(options);
        return Options.Create(options);
    }
}
