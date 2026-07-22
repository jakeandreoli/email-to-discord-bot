using System.Text.RegularExpressions;
using EmailToDiscord.Configuration;
using EmailToDiscord.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace EmailToDiscord.Services;

public class EmailSender
{
    private readonly ContentConverter _converter;
    private readonly ILogger<EmailSender> _logger;

    private const string TemplateMessageToken = "{{message}}";

    private static readonly Regex RefRegex = new(@"\s*\[ref:\s*\d+\]", RegexOptions.IgnoreCase);
    private static readonly Regex RePrefixRegex = new(@"^(?:\s*(?:Re|Fwd|Fw)\s*:\s*)+", RegexOptions.IgnoreCase);

    public EmailSender(ContentConverter converter, ILogger<EmailSender> logger)
    {
        _converter = converter;
        _logger = logger;
    }

    public async Task SendReplyAsync(
        MailboxConfig mailbox,
        string toAddress,
        string originalSubject,
        string markdownBody,
        ulong threadId,
        string? inReplyToMessageId,
        IEnumerable<EmailAttachment> attachments,
        string? footer = null,
        CancellationToken ct = default
    )
    {
        var fromName = string.IsNullOrWhiteSpace(mailbox.Smtp.FromName) ? mailbox.Email : mailbox.Smtp.FromName;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, mailbox.Email));
        message.To.Add(MailboxAddress.Parse(toAddress));

        var cleanSubject = NormalizeSubject(originalSubject);
        message.Subject = $"Re: {cleanSubject} [ref:{threadId}]";

        if (!string.IsNullOrEmpty(inReplyToMessageId))
        {
            var clean = inReplyToMessageId.Trim().Trim('<', '>');
            message.InReplyTo = clean;
            message.References.Add(clean);
        }

        var bodyMarkdown = ApplyTemplate(mailbox, markdownBody);

        if (!string.IsNullOrWhiteSpace(footer))
            bodyMarkdown = $"{bodyMarkdown}\n\n---\n\n{footer.Trim()}";

        var builder = new BodyBuilder
        {
            TextBody = bodyMarkdown,
            HtmlBody = _converter.MarkdownToHtml(bodyMarkdown),
        };

        foreach (var att in attachments)
        {
            ContentType ct2;
            try 
            {
                ct2 = ContentType.Parse(att.ContentType); 
            }
            catch
            { 
                ct2 = new ContentType("application", "octet-stream");
            }

            builder.Attachments.Add(att.FileName, att.Content, ct2);
        }

        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        var secureOption = mailbox.Smtp.UseStarttls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;

        await client.ConnectAsync(mailbox.Smtp.Host, mailbox.Smtp.Port, secureOption, ct);
        await client.AuthenticateAsync(mailbox.Smtp.Username, mailbox.Smtp.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);

        _logger.LogInformation(
            "Sent reply for thread {ThreadId} to {To} via {Mailbox}",
            threadId, 
            toAddress, 
            mailbox.Email
        );
    }

    private static string NormalizeSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return "(no subject)";

        var s = RefRegex.Replace(subject, "").Trim();
        s = RePrefixRegex.Replace(s, "").Trim();

        return string.IsNullOrWhiteSpace(s) ? "(no subject)" : s;
    }

    private string ApplyTemplate(MailboxConfig mailbox, string markdownBody)
    {
        var template = LoadTemplate(mailbox);
        if (string.IsNullOrEmpty(template))
            return markdownBody;

        if (!template.Contains(TemplateMessageToken, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Mailbox {Mailbox} has a template without a {Token} placeholder; sending body without it.",
                mailbox.Email, 
                TemplateMessageToken
            );

            return markdownBody;
        }

        return template.Replace(TemplateMessageToken, markdownBody);
    }

    private string LoadTemplate(MailboxConfig mailbox)
    {
        if (string.IsNullOrWhiteSpace(mailbox.TemplatePath)) 
            return "";

        try
        {
            return File.ReadAllText(mailbox.TemplatePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read template at {Path} for mailbox {Mailbox}; sending body without template.",
                mailbox.TemplatePath, 
                mailbox.Email
            );

            return "";
        }
    }
}
