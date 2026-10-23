using System.Text;
using System.Text.RegularExpressions;
using FakeServerEmail.Core;
using MimeKit;
using MimeKit.IO;

namespace FakeServerEmail.Web;

public sealed record AttachmentInfo(int Index, string FileName, string ContentType, long Size, string? ContentId, bool Inline);

public sealed record HeaderInfo(string Name, string Value);

public sealed record MessageDetail(
    StoredMessage Summary,
    string Subject,
    string From,
    string To,
    string Cc,
    string Bcc,
    string ReplyTo,
    DateTimeOffset? Date,
    string? MessageId,
    bool HasHtml,
    string? Text,
    List<HeaderInfo> Headers,
    List<AttachmentInfo> Attachments,
    string? ParseError);

public static partial class MessageView
{
    private static MimeMessage? Parse(byte[] raw)
    {
        try
        {
            using var ms = new MemoryStream(raw, false);
            return MimeMessage.Load(ms);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static MessageDetail Build(StoredMessage summary, byte[] raw)
    {
        var message = Parse(raw);
        if (message is null)
            return new MessageDetail(summary, summary.Subject, summary.From, summary.To, summary.Cc, "", "", summary.Date, null, false,
                Encoding.UTF8.GetString(raw), [], [], "Não foi possível interpretar a mensagem; exibindo o conteúdo bruto.");

        var parts = message.BodyParts.ToList();
        var attachments = new List<AttachmentInfo>();
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] is not MimePart part)
                continue;
            var isBodyText = part is TextPart text && !part.IsAttachment && (text.IsPlain || text.IsHtml);
            if (isBodyText)
                continue;
            attachments.Add(new AttachmentInfo(i, FileName(part, i), part.ContentType.MimeType, MeasureSize(part), part.ContentId, !part.IsAttachment));
        }

        return new MessageDetail(
            summary,
            message.Subject ?? "",
            message.From.ToString(),
            message.To.ToString(),
            message.Cc.ToString(),
            message.Bcc.ToString(),
            message.ReplyTo.ToString(),
            message.Headers.Contains(HeaderId.Date) ? message.Date : null,
            message.MessageId,
            message.HtmlBody is not null,
            message.TextBody ?? (message.HtmlBody is not null ? null : ""),
            message.Headers.Select(h => new HeaderInfo(h.Field, h.Value)).ToList(),
            attachments,
            null);
    }

    public static string? Html(string id, byte[] raw)
    {
        var message = Parse(raw);
        var html = message?.HtmlBody;
        if (html is null)
            return null;
        return CidRegex().Replace(html, m => $"/api/messages/{id}/cid/{Uri.EscapeDataString(m.Groups[1].Value)}");
    }

    [GeneratedRegex(@"cid:([^""'\s>)]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CidRegex();

    public static (byte[] Content, string ContentType, string FileName)? Part(byte[] raw, int index)
    {
        var parts = Parse(raw)?.BodyParts.ToList();
        if (parts is null || index < 0 || index >= parts.Count || parts[index] is not MimePart part)
            return null;
        return (Decode(part), part.ContentType.MimeType, FileName(part, index));
    }

    public static (byte[] Content, string ContentType)? ByContentId(byte[] raw, string contentId)
    {
        var wanted = contentId.Trim('<', '>');
        var part = Parse(raw)?.BodyParts.OfType<MimePart>().FirstOrDefault(p => p.ContentId?.Trim('<', '>').Equals(wanted, StringComparison.OrdinalIgnoreCase) == true);
        return part is null ? null : (Decode(part), part.ContentType.MimeType);
    }

    private static byte[] Decode(MimePart part)
    {
        using var ms = new MemoryStream();
        part.Content?.DecodeTo(ms);
        return ms.ToArray();
    }

    private static long MeasureSize(MimePart part)
    {
        try
        {
            using var measure = new MeasuringStream();
            part.Content?.DecodeTo(measure);
            return measure.Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string FileName(MimePart part, int index)
    {
        if (!string.IsNullOrWhiteSpace(part.FileName))
            return part.FileName;
        var extension = MimeTypes.TryGetExtension(part.ContentType.MimeType, out var ext) ? ext : ".bin";
        return $"parte-{index + 1}{extension}";
    }
}
