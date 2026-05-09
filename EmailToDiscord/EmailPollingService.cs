using System.Text.RegularExpressions;
using EmailToDiscord.Configuration;
using EmailToDiscord.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace EmailToDiscord.Services;

public class EmailPollingService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly StateStore _state;
    private readonly DiscordBotService _discord;
    private readonly ContentConverter _converter;
    private readonly ILogger<EmailPollingService> _logger;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private static readonly Regex RefRegex = new(@"\[ref:\s*(\d+)\]", RegexOptions.IgnoreCase);
    private static readonly Regex CidImageRegex = new(@"!\[[^\]]*\]\(cid:([^)\s]+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex[] QuoteStartPatterns =
    {
        new(@"^On .+?\bwrote:\s*$(?=\s*\r?\n\s*>)", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline),
        new(@"^-{2,}\s*Original Message\s*-{2,}\s*$", RegexOptions.Compiled | RegexOptions.Multiline),
        new(@"^_{5,}\s*$", RegexOptions.Compiled | RegexOptions.Multiline),
        new(@"^From:\s.+\r?\nSent:\s.+", RegexOptions.Compiled | RegexOptions.Multiline),
    };

    public EmailPollingService(
        AppConfig config,
        StateStore state,
        DiscordBotService discord,
        ContentConverter converter,
        ILogger<EmailPollingService> logger
    )
    {
        _config = config;
        _state = state;
        _discord = discord;
        _converter = converter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _discord.WaitUntilReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(30, _config.Polling.IntervalSeconds));
        _logger.LogInformation("Email polling started; interval {Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllMailboxesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<int> PollAllMailboxesAsync(CancellationToken ct)
    {
        await _pollLock.WaitAsync(ct);
        try
        {
            int total = 0;
            foreach (var mailbox in _config.Mailboxes)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    total += await PollMailboxAsync(mailbox, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error polling mailbox {Mailbox}", mailbox.Email);
                }
            }

            await _discord.SetLastCheckedAsync(DateTimeOffset.Now);
            return total;
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private async Task<int> PollMailboxAsync(MailboxConfig mailbox, CancellationToken ct)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(mailbox.Imap.Host, mailbox.Imap.Port, mailbox.Imap.UseSsl, ct);
        await client.AuthenticateAsync(mailbox.Imap.Username, mailbox.Imap.Password, ct);

        var folder = await client.GetFolderAsync(mailbox.Imap.Folder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var stateKey = mailbox.Email;
        var lastUid = _state.GetLastUid(stateKey);

        IList<UniqueId> uids;
        if (lastUid.HasValue)
        {
            var range = new UniqueIdRange(new UniqueId(lastUid.Value + 1), UniqueId.MaxValue);
            uids = await folder.SearchAsync(SearchQuery.Uids(range), ct);
        }
        else
        {
            uids = await folder.SearchAsync(SearchQuery.NotSeen, ct);

            if (folder.UidNext.HasValue && folder.UidNext.Value.Id > 0)
                _state.SetLastUid(stateKey, folder.UidNext.Value.Id - 1);
        }

        if (uids.Count == 0)
        {
            await client.DisconnectAsync(true, ct);
            return 0;
        }

        _logger.LogInformation("Mailbox {Mailbox}: {Count} new message(s)", mailbox.Email, uids.Count);

        foreach (var uid in uids.OrderBy(u => u.Id))
        {
            if (ct.IsCancellationRequested) 
                break;

            try
            {
                var mime = await folder.GetMessageAsync(uid, ct);
                var email = ParseMimeMessage(mime);

                if (string.Equals(email.FromAddress, mailbox.Email, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Skipping self-sent mail UID {Uid} in {Mailbox}", uid, mailbox.Email);
                    _state.SetLastUid(stateKey, uid.Id);
                    continue;
                }

                await _discord.HandleIncomingEmailAsync(mailbox, email, ct);
                _state.SetLastUid(stateKey, uid.Id);
            }
            catch (OperationCanceledException) 
            {
                throw; 
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing UID {Uid} in {Mailbox}", uid, mailbox.Email);
                _state.SetLastUid(stateKey, uid.Id);
            }
        }

        await client.DisconnectAsync(true, ct);
        return uids.Count;
    }

    private IncomingEmail ParseMimeMessage(MimeMessage mime)
    {
        var fromAddr = mime.From.OfType<MailboxAddress>().FirstOrDefault();

        var subject = mime.Subject ?? "";

        ulong? refThreadId = null;
        var refMatch = RefRegex.Match(subject);
        if (refMatch.Success && ulong.TryParse(refMatch.Groups[1].Value, out var parsedId))
            refThreadId = parsedId;

        string body = !string.IsNullOrEmpty(mime.HtmlBody)
            ? _converter.HtmlToMarkdown(mime.HtmlBody)
            : (mime.TextBody ?? "");

        if (refThreadId.HasValue)
            body = StripQuotedReply(body);

        var referencedCids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in CidImageRegex.Matches(body))
            referencedCids.Add(m.Groups[1].Value);

        body = CidImageRegex.Replace(body, "").Trim();

        var attachments = new List<EmailAttachment>();

        foreach (var part in mime.BodyParts.OfType<MimePart>())
        {
            if (part.IsAttachment) 
                continue;

            if (string.IsNullOrEmpty(part.ContentId)) 
                continue;

            if (!part.ContentType.IsMimeType("image", "*")) 
                continue;

            if (!referencedCids.Contains(part.ContentId)) 
                continue;

            if (part.Content == null) 
                continue;

            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            attachments.Add(new EmailAttachment
            {
                FileName = part.FileName ?? $"inline-{part.ContentId}",
                ContentType = part.ContentType.MimeType,
                Content = ms.ToArray(),
            });
        }

        foreach (var attEntity in mime.Attachments)
        {
            if (attEntity is MimePart part)
            {
                if (part.Content == null) 
                    continue;

                using var ms = new MemoryStream();
                part.Content.DecodeTo(ms);
                attachments.Add(new EmailAttachment
                {
                    FileName = part.FileName ?? "attachment",
                    ContentType = part.ContentType.MimeType,
                    Content = ms.ToArray(),
                });
            }
            else if (attEntity is MessagePart msgPart)
            {
                if (msgPart.Message == null) 
                    continue;

                using var ms = new MemoryStream();
                msgPart.Message.WriteTo(ms);
                attachments.Add(new EmailAttachment
                {
                    FileName = "forwarded-message.eml",
                    ContentType = "message/rfc822",
                    Content = ms.ToArray(),
                });
            }
        }

        return new IncomingEmail
        {
            MessageId = mime.MessageId ?? "",
            FromAddress = fromAddr?.Address ?? "",
            FromName = fromAddr?.Name ?? "",
            Subject = subject,
            Body = body,
            Attachments = attachments,
            ReferencedThreadId = refThreadId,
            Date = mime.Date,
        };
    }

    private static string StripQuotedReply(string body)
    {
        if (string.IsNullOrEmpty(body)) 
            return body;

        int earliest = -1;
        foreach (var pattern in QuoteStartPatterns)
        {
            var match = pattern.Match(body);
            if (match.Success && (earliest == -1 || match.Index < earliest))
                earliest = match.Index;
        }

        if (earliest == -1)
        {
            var lines = body.Split('\n');
            int firstQuotedBlock = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith(">"))
                {
                    firstQuotedBlock = i;
                    break;
                }
            }

            if (firstQuotedBlock > 0)
            {
                return string.Join("\n", lines.Take(firstQuotedBlock)).TrimEnd();
            }

            return body;
        }

        return body.Substring(0, earliest).TrimEnd();
    }
}
