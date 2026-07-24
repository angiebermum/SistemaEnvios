using System.Net;

namespace ECS.CommissionsMailer.Services;

public static class EmailBodyBuilder
{
    public static string BuildHtml(string plainText, SignatureImageInfo? signature = null, string? contentId = null)
    {
        var encodedMessage = WebUtility.HtmlEncode(plainText ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal);

        var signatureHtml = string.Empty;
        if (signature is not null && !string.IsNullOrWhiteSpace(contentId))
        {
            var safeCid = WebUtility.HtmlEncode(contentId);
            signatureHtml = $"<div style=\"margin-top:24px;\"><img src=\"cid:{safeCid}\" alt=\"Firma\" width=\"{signature.DisplayWidth}\" style=\"display:block;max-width:600px;width:auto;height:auto;border:0;\"></div>";
        }

        return $"<html><body style=\"font-family:'Segoe UI',Arial,sans-serif;font-size:11pt;color:#1F2937;line-height:1.45;\"><div>{encodedMessage}</div>{signatureHtml}</body></html>";
    }
}
