using System.Buffers;
using SmtpServer;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace MailSink;

/// <summary>
/// Adapter between SmtpServer and <see cref="IMailCapture"/>: unwraps the session and transaction
/// into plain values, then translates the capture outcome back into an SMTP reply. Nothing is
/// relayed - the message only ever goes to the configured writer.
/// </summary>
public sealed class EmlMessageStore(IMailCapture capture) : MessageStore
{
    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context,
        IMessageTransaction transaction,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken)
    {
        var message = new IncomingMessage(
            Raw: buffer.ToArray(),
            From: Format(transaction.From),
            To: [.. transaction.To.Select(Format)],
            ClientAddress: SessionClient.Describe(context),
            AccountFolder: SessionAccount.Folder(context),
            Account: SessionAccount.Current(context)?.Key ?? string.Empty);

        var result = await capture.CaptureAsync(message, cancellationToken);

        return result.Succeeded
            ? SmtpResponse.Ok
            : new SmtpResponse(SmtpReplyCode.TransactionFailed, "Could not store message");
    }

    private static string Format(SmtpServer.Mail.IMailbox? mailbox) =>
        mailbox is null || string.IsNullOrEmpty(mailbox.Host)
            ? mailbox?.User ?? string.Empty
            : $"{mailbox.User}@{mailbox.Host}";
}
