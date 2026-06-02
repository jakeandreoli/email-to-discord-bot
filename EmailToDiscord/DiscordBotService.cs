using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using EmailToDiscord.Configuration;
using EmailToDiscord.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailToDiscord.Services;

public class DiscordBotService : IHostedService
{
    private readonly AppConfig _config;
    private readonly EmailSender _sender;
    private readonly ContentConverter _converter;
    private readonly ThreadStore _threadStore;
    private readonly CannedReplyService _cannedReplies;
    // Lazy-resolved to break the DI cycle: EmailPollingService already depends on this service.
    private readonly IServiceProvider _services;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly DiscordSocketClient _client;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private const string CannedReplyCommandName = "canned-reply";
    private const string CannedReplyTagOption = "tag";
    private const string CannedYesPrefix = "cr:y:";
    private const string CannedNoId = "cr:n";
    private const string PendingReplyYesPrefix = "pr:y:";
    private const string PendingReplyNoPrefix = "pr:n:";
    private const string RefreshEmailCommandName = "refresh-email";
    private const int MaxAutocompleteChoices = 25;
    private const string MetaPrefix = "[bridge]";
    private static readonly TimeSpan PendingReplyTtl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, PendingReply> _pendingReplies = new();

    private record PendingReply(
        string Body,
        IReadOnlyList<EmailAttachment> Attachments,
        ulong ThreadId,
        ulong TriggerMessageId,
        DateTimeOffset Expires
    );

    // Discord limits
    private const int MaxEmbedDescription = 4000; // cap is 4096; leave room
    private const int MaxForumTitle = 100;
    private const int MaxEmbedTitle = 256;

    private static readonly Regex MetaFromRegex = new(@"from=(\S+)", RegexOptions.Compiled);
    private static readonly Regex MetaMsgIdRegex = new(@"msgid=(\S+)", RegexOptions.Compiled);
    private static readonly Regex MetaSubjectRegex = new(@"subj=(.+)$", RegexOptions.Compiled);

    public DiscordBotService(
        AppConfig config,
        EmailSender sender,
        ContentConverter converter,
        ThreadStore threadStore,
        CannedReplyService cannedReplies,
        IServiceProvider services,
        ILogger<DiscordBotService> logger
    )
    {
        _config = config;
        _sender = sender;
        _converter = converter;
        _threadStore = threadStore;
        _cannedReplies = cannedReplies;
        _services = services;
        _logger = logger;

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
            LogLevel = LogSeverity.Info,
            AlwaysDownloadUsers = false,
            MessageCacheSize = 50,
            UseInteractionSnowflakeDate = false,
        });

        _client.Log += OnDiscordLog;
        _client.Ready += () => 
        { 
            _ = Task.Run(OnReady); 
            return Task.CompletedTask; 
        };

        _client.MessageReceived += msg =>
        { 
            _ = Task.Run(() => OnMessageReceivedAsync(msg));
            return Task.CompletedTask;
        };

        _client.SlashCommandExecuted += cmd =>
        { 
            _ = Task.Run(() => OnSlashCommandAsync(cmd));
            return Task.CompletedTask;
        };

        _client.AutocompleteExecuted += i =>
        { 
            _ = Task.Run(() => OnAutocompleteAsync(i));
            return Task.CompletedTask;
        };

        _client.ButtonExecuted += c =>
        { 
            _ = Task.Run(() => OnButtonAsync(c));
            return Task.CompletedTask;
        };
    }

    public Task WaitUntilReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);

    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.Discord.Token))
        {
            throw new InvalidOperationException("Discord token is not configured.");
        }

        await _client.LoginAsync(TokenType.Bot, _config.Discord.Token);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _client.StopAsync();
        await _client.LogoutAsync();
        _client.Dispose();
    }

    private async Task OnReady()
    {
        _logger.LogInformation("Discord bot ready as {User} ({Id})", _client.CurrentUser, _client.CurrentUser?.Id);
        await RegisterSlashCommandsAsync();
        await PrewarmRestAsync();
        await SetActivityAsync("Filing your mail");
        _ready.TrySetResult();
    }

    /// <summary>
    /// Updates the bot's custom status. Called by the polling service after each cycle so users
    /// can see when mail was last checked at a glance.
    /// </summary>
    public Task SetLastCheckedAsync(DateTimeOffset when) =>
        SetActivityAsync($"Filing your mail [Last checked: {when.ToLocalTime():HH:mm}]");

    private async Task SetActivityAsync(string text)
    {
        try
        {
            await _client.SetActivityAsync(new CustomStatusGame(text));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to set activity to {Text}", text);
        }
    }

    private async Task PrewarmRestAsync()
    {
        try
        {
            await _client.GetApplicationInfoAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "REST pre-warm failed (non-fatal).");
        }
    }

    private async Task RegisterSlashCommandsAsync()
    {
        if (_config.Discord.GuildIds.Count == 0)
        {
            _logger.LogInformation("No guild_ids configured; skipping slash command registration.");
            return;
        }

        var cannedReplyCommand = new SlashCommandBuilder()
            .WithName(CannedReplyCommandName)
            .WithDescription("Send a canned reply to the customer in this thread.")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName(CannedReplyTagOption)
                .WithDescription("Which canned reply to send.")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .WithAutocomplete(true))
            .Build();

        var refreshEmailCommand = new SlashCommandBuilder()
            .WithName(RefreshEmailCommandName)
            .WithDescription("Force an immediate poll of all configured mailboxes.")
            .Build();

        var commands = new[] { cannedReplyCommand, refreshEmailCommand };

        foreach (var gid in _config.Discord.GuildIds)
        {
            var guild = _client.GetGuild(gid);
            if (guild == null)
            {
                _logger.LogWarning("Guild {GuildId} not found; can't register slash commands there.", gid);
                continue;
            }

            foreach (var command in commands)
            {
                try
                {
                    await guild.CreateApplicationCommandAsync(command);
                    _logger.LogInformation(
                        "Registered /{Command} in guild {Guild} ({GuildId})",
                        command.Name.Value, guild.Name, gid);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to register /{Command} in guild {GuildId}", command.Name.Value, gid);
                }
            }
        }
    }

    private Task OnDiscordLog(LogMessage msg)
    {
        var level = msg.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information,
        };

        _logger.Log(level, msg.Exception, "[Discord] {Source}: {Message}", msg.Source, msg.Message);
        return Task.CompletedTask;
    }

    public async Task HandleIncomingEmailAsync(MailboxConfig mailbox, IncomingEmail email, CancellationToken ct)
    {
        var channel = _client.GetChannel(mailbox.ForumChannelId);
        if (channel is not IForumChannel forum)
        {
            _logger.LogError("Channel {ChannelId} for {Mailbox} - Can't access",
                mailbox.ForumChannelId, mailbox.Email);
            return;
        }

        if (email.ReferencedThreadId.HasValue)
        {
            SocketThreadChannel? thread = _client.GetChannel(email.ReferencedThreadId.Value) as SocketThreadChannel;
            if (thread != null && thread.ParentChannel?.Id == mailbox.ForumChannelId)
            {
                await SendIncomingToThreadAsync(mailbox, thread, email, ct);
                return;
            }

            _logger.LogWarning(
                "Referenced thread {ThreadId} not found in forum {ForumId}; creating a new post instead",
                email.ReferencedThreadId,
                mailbox.ForumChannelId
            );
        }

        await CreateNewForumPostAsync(mailbox, forum, email, ct);
    }

    private async Task CreateNewForumPostAsync(MailboxConfig mailbox, IForumChannel forum, IncomingEmail email, CancellationToken ct)
    {
        var (embed, longBodyAttachment) = BuildIncomingEmbed(email);
        var attachments = BuildDiscordAttachments(email.Attachments, longBodyAttachment);

        try
        {
            var titleSource = string.IsNullOrWhiteSpace(email.Subject)
                ? $"(no subject) — {email.FromAddress}"
                : email.Subject;
            var title = TruncateForTitle(titleSource, MaxForumTitle);
            IThreadChannel post;

            if (attachments.Count > 0)
            {
                post = await forum.CreatePostWithFilesAsync(
                    title,
                    attachments,
                    text: email.FromAddress,
                    embeds: [embed],
                    allowedMentions: AllowedMentions.None,
                    options: new RequestOptions { CancelToken = ct });
            }
            else
            {
                post = await forum.CreatePostAsync(
                    title,
                    text: email.FromAddress,
                    embeds: [embed],
                    allowedMentions: AllowedMentions.None,
                    options: new RequestOptions { CancelToken = ct });
            }

            _threadStore.RecordInbound(post.Id, email.FromAddress, email.Subject ?? "", mailbox.Email, email.MessageId);
            _logger.LogInformation("Created forum post {ThreadId} from {From}", post.Id, email.FromAddress);
        }
        finally
        {
            foreach (var a in attachments) a.Dispose();
        }
    }

    private async Task SendIncomingToThreadAsync(MailboxConfig mailbox, SocketThreadChannel thread, IncomingEmail email, CancellationToken ct)
    {
        var (embed, longBodyAttachment) = BuildIncomingEmbed(email);
        var attachments = BuildDiscordAttachments(email.Attachments, longBodyAttachment);

        try
        {
            if (attachments.Count > 0)
            {
                await thread.SendFilesAsync(
                    attachments,
                    embeds: [embed],
                    options: new RequestOptions { CancelToken = ct });
            }
            else
            {
                await thread.SendMessageAsync(
                    embed: embed,
                    options: new RequestOptions { CancelToken = ct });
            }

            _threadStore.RecordInbound(
                thread.Id,
                email.FromAddress,
                email.Subject ?? "",
                mailbox.Email,
                email.MessageId
            );

            _logger.LogInformation("Posted reply email to thread {ThreadId} from {From}", thread.Id, email.FromAddress);
        }
        finally
        {
            foreach (var a in attachments) a.Dispose();
        }
    }

    private (Embed Embed, EmailAttachment? BodyFile) BuildIncomingEmbed(IncomingEmail email)
    {
        var displayName = string.IsNullOrWhiteSpace(email.FromName)
            ? email.FromAddress
            : $"{email.FromName} <{email.FromAddress}>";

        EmailAttachment? bodyAttachment = null;
        var description = email.Body;
        if (description.Length > MaxEmbedDescription)
        {
            bodyAttachment = new EmailAttachment
            {
                FileName = "email-body.md",
                ContentType = "text/markdown",
                Content = Encoding.UTF8.GetBytes(email.Body),
            };

            description = description.Substring(0, MaxEmbedDescription - 80)
                          + "\n\n*…truncated; full body attached as `email-body.md`*";
        }
        else if (string.IsNullOrWhiteSpace(description))
        {
            description = "*(no message body)*";
        }

        var builder = new EmbedBuilder()
            .WithAuthor(displayName)
            .WithTitle(TruncateForTitle(email.Subject, MaxEmbedTitle))
            .WithDescription(description)
            .WithTimestamp(email.Date == default ? DateTimeOffset.UtcNow : email.Date);

        return (builder.Build(), bodyAttachment);
    }

    private static List<FileAttachment> BuildDiscordAttachments(
        IEnumerable<EmailAttachment> emailAttachments,
        EmailAttachment? extra
    )
    {
        var list = new List<FileAttachment>();
        foreach (var att in emailAttachments)
            list.Add(new FileAttachment(new MemoryStream(att.Content), att.FileName));

        if (extra != null)
            list.Add(new FileAttachment(new MemoryStream(extra.Content), extra.FileName));

        return list;
    }

    private static string TruncateForTitle(string? input, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "(no subject)";

        input = input.Replace("\n", " ")
                .Replace("\r", " ")
                .Trim();

        return input.Length <= maxLen ? input : string.Concat(input.AsSpan(0, maxLen - 3), "...");
    }

    private async Task OnMessageReceivedAsync(SocketMessage rawMessage)
    {
        try
        {
            if (rawMessage is not SocketUserMessage msg)
                return;
            
            if (msg.Author.IsBot)
                return;
            
            if (msg.Channel is not SocketThreadChannel thread)
                return;
            
            if (thread.ParentChannel == null)
                return;

            var mailbox = _config.Mailboxes.FirstOrDefault(m => m.ForumChannelId == thread.ParentChannel.Id);
            if (mailbox == null)
                return;

            var prefix = _config.Discord.CommandPrefix;
            var content = msg.Content ?? "";

            bool isCommand = content.TrimStart()
                .StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

            bool isReplyToBot = msg.ReferencedMessage != null
                && msg.ReferencedMessage.Author.Id == _client.CurrentUser.Id;

            if (!isCommand && !isReplyToBot)
                return;

            string replyBody;
            if (isCommand)
            {
                var trimmed = content.TrimStart();
                replyBody = trimmed.Substring(prefix.Length).TrimStart(':', ' ', '\t');
            }
            else
            {
                replyBody = content;
            }

            if (string.IsNullOrWhiteSpace(replyBody) && msg.Attachments.Count == 0)
            {
                await msg.AddReactionAsync(new Emoji("⚠️"));
                _logger.LogWarning("Reply trigger in thread {ThreadId} had no body or attachments", thread.Id);
                return;
            }

            var emailAttachments = await DownloadDiscordAttachmentsAsync(msg.Attachments);

            PruneExpiredPending();
            var record = _threadStore.Get(thread.Id);
            if (record?.AwaitingCustomer == true)
            {
                await PostPendingReplyConfirmationAsync(thread, msg, replyBody, emailAttachments);
                return;
            }

            try
            {
                await SendThreadReplyAsync(thread, mailbox, replyBody, emailAttachments, CancellationToken.None);
                await msg.AddReactionAsync(new Emoji("📧"));
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("{Message} (thread {ThreadId})", ex.Message, thread.Id);
                await msg.AddReactionAsync(new Emoji("❌"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email reply for thread {ThreadId}", thread.Id);
                await msg.AddReactionAsync(new Emoji("❌"));
                try
                {
                    await thread.SendMessageAsync($"⚠️ Failed to send email: `{ex.Message}`");
                }
                catch
                { 
                    // fuck it, go for headshots.    
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in OnMessageReceivedAsync");
        }
    }

    private async Task SendThreadReplyAsync(
        SocketThreadChannel thread,
        MailboxConfig mailbox,
        string replyBody,
        IEnumerable<EmailAttachment> attachments,
        CancellationToken ct
    )
    {
        var meta = await ResolveThreadMetaAsync(thread, mailbox.Email);
        if (meta == null || string.IsNullOrEmpty(meta.CustomerEmail))
            throw new InvalidOperationException("Could not resolve customer email for this thread.");

        await _sender.SendReplyAsync(
            mailbox,
            meta.CustomerEmail,
            meta.OriginalSubject ?? "Support",
            replyBody,
            thread.Id,
            meta.LatestMessageId,
            attachments,
            ct
        );

        _threadStore.RecordOutbound(thread.Id);
    }

    private void PruneExpiredPending()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _pendingReplies)
        {
            if (kv.Value.Expires < now)
                _pendingReplies.TryRemove(kv.Key, out _);
        }
    }

    private async Task PostPendingReplyConfirmationAsync(
        SocketThreadChannel thread,
        SocketUserMessage trigger,
        string body,
        IReadOnlyList<EmailAttachment> attachments
    )
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        _pendingReplies[token] = new PendingReply(
            body,
            attachments,
            thread.Id,
            trigger.Id,
            DateTimeOffset.UtcNow + PendingReplyTtl
        );

        var components = new ComponentBuilder()
            .WithButton("Send anyway", PendingReplyYesPrefix + token, ButtonStyle.Danger)
            .WithButton("Cancel", PendingReplyNoPrefix + token, ButtonStyle.Secondary)
            .Build();

        var allowed = new AllowedMentions();
        allowed.UserIds.Add(trigger.Author.Id);

        await thread.SendMessageAsync(
            text: $"<@{trigger.Author.Id}>: The customer hasn't replied to your last email. Send anyway?",
            components: components,
            messageReference: new MessageReference(trigger.Id),
            allowedMentions: allowed
        );

        await trigger.AddReactionAsync(new Emoji("⚠️"));
    }

    private async Task<ThreadMeta?> ResolveThreadMetaAsync(SocketThreadChannel thread, string mailboxEmail)
    {
        var record = _threadStore.Get(thread.Id);
        if (record != null)
            return new ThreadMeta(record.CustomerEmail, record.OriginalSubject, record.LatestInboundMsgId);

        string? latestMsgId = null;
        string? customerEmail = null;
        string? originalSubject = null;

        try
        {
            var messages = await thread.GetMessagesAsync(50).FlattenAsync();
            foreach (var m in messages.OrderByDescending(x => x.Timestamp))
            {
                if (m.Author.Id != _client.CurrentUser.Id)
                    continue;
                
                var parsed = ExtractMetaFromMessage(m);
                if (parsed == null)
                    continue;

                latestMsgId ??= parsed.LatestMessageId;
                if (!string.IsNullOrEmpty(parsed.CustomerEmail))
                {
                    customerEmail ??= parsed.CustomerEmail;
                    originalSubject ??= parsed.OriginalSubject;
                }

                if (latestMsgId != null && customerEmail != null)
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read thread {ThreadId} history", thread.Id);
        }

        if (customerEmail == null)
        {
            try
            {
                var starter = await thread.GetMessageAsync(thread.Id);
                if (starter != null)
                {
                    var parsed = ExtractMetaFromMessage(starter);
                    if (parsed != null)
                    {
                        if (!string.IsNullOrEmpty(parsed.CustomerEmail))
                        {
                            customerEmail = parsed.CustomerEmail;
                            originalSubject ??= parsed.OriginalSubject;
                        }

                        latestMsgId ??= parsed.LatestMessageId;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch starter message for thread {ThreadId}", thread.Id);
            }
        }

        if (string.IsNullOrEmpty(customerEmail))
            return null;

        _threadStore.RecordInbound(
            thread.Id,
            customerEmail,
            originalSubject ?? "",
            mailboxEmail,
            latestMsgId
        );

        return new ThreadMeta(customerEmail, originalSubject, latestMsgId);
    }

    private static ThreadMeta? ExtractMetaFromMessage(IMessage msg)
    {
        foreach (var embed in msg.Embeds)
        {
            var footer = embed.Footer?.Text;
            if (string.IsNullOrEmpty(footer) || !footer.StartsWith(MetaPrefix))
                continue;

            var fromMatch = MetaFromRegex.Match(footer);
            var msgIdMatch = MetaMsgIdRegex.Match(footer);
            var subjMatch = MetaSubjectRegex.Match(footer);

            var fromEmail = fromMatch.Success ? fromMatch.Groups[1].Value : "";
            var msgId = msgIdMatch.Success && msgIdMatch.Groups[1].Value != "-"
                ? msgIdMatch.Groups[1].Value
                : null;

            var subject = subjMatch.Success ? subjMatch.Groups[1].Value.Trim() : null;

            if (string.IsNullOrEmpty(fromEmail) && string.IsNullOrEmpty(msgId))
                continue;

            return new ThreadMeta(fromEmail, subject, msgId);
        }
        return null;
    }

    private async Task<List<EmailAttachment>> DownloadDiscordAttachmentsAsync(IReadOnlyCollection<IAttachment> attachments)
    {
        var result = new List<EmailAttachment>();
        if (attachments.Count == 0)
            return result;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        foreach (var a in attachments)
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(a.Url);
                result.Add(new EmailAttachment
                {
                    FileName = a.Filename,
                    ContentType = a.ContentType ?? "application/octet-stream",
                    Content = bytes,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download Discord attachment {File}", a.Filename);
            }
        }
        return result;
    }

    private record ThreadMeta(string CustomerEmail, string? OriginalSubject, string? LatestMessageId);

    private async Task OnAutocompleteAsync(SocketAutocompleteInteraction interaction)
    {
        try
        {
            if (interaction.Data.CommandName != CannedReplyCommandName)
                return;

            var current = interaction.Data.Current.Value?.ToString() ?? "";
            var matches = _cannedReplies.ListTags()
                .Where(t => t.Contains(current, StringComparison.OrdinalIgnoreCase))
                .Take(MaxAutocompleteChoices)
                .Select(t => new AutocompleteResult(t, t));

            await interaction.RespondAsync(matches);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autocomplete handler failed");
        }
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        var snowflakeTime = SnowflakeUtils.FromSnowflake(command.Id);
        var dispatchLagMs = (DateTimeOffset.UtcNow - snowflakeTime).TotalMilliseconds;

        try
        {
            if (command.Data.Name == RefreshEmailCommandName)
            {
                await HandleRefreshEmailAsync(command, dispatchLagMs);
                return;
            }

            if (command.Data.Name != CannedReplyCommandName)
                return;

            var deferStart = DateTimeOffset.UtcNow;
            try
            {
                await command.DeferAsync(ephemeral: true);
            }
            catch (Discord.Net.HttpException dex) when ((int)dex.DiscordCode == 10062)
            {
                var deferElapsedMs = (DateTimeOffset.UtcNow - deferStart).TotalMilliseconds;
                _logger.LogWarning(
                    "Interaction {Id} expired before defer could be acknowledged. " +
                    "dispatch_lag={DispatchLagMs:F0}ms, defer_call={DeferElapsedMs:F0}ms",
                    command.Id, dispatchLagMs, deferElapsedMs
                );
                return;
            }

            if (command.Channel is not SocketThreadChannel thread || thread.ParentChannel == null || _config.Mailboxes.All(m => m.ForumChannelId != thread.ParentChannel.Id))
            {
                await command.ModifyOriginalResponseAsync(p => p.Content = "This command must be used inside a support thread.");
                return;
            }

            var tag = command.Data.Options.FirstOrDefault(o => o.Name == CannedReplyTagOption)?.Value as string;
            if (string.IsNullOrWhiteSpace(tag))
            {
                await command.ModifyOriginalResponseAsync(p => p.Content = "Pick a canned reply tag.");
                return;
            }

            var body = _cannedReplies.Read(tag);
            if (body == null)
            {
                await command.ModifyOriginalResponseAsync(p => p.Content = $"No canned reply found for tag `{tag}`.");
                return;
            }

            var preview = body.Length > MaxEmbedDescription
                ? string.Concat(body.AsSpan(0, MaxEmbedDescription - 20), "\n\n*...truncated*")
                : body;

            var embed = new EmbedBuilder()
                .WithTitle($"Canned reply: {tag}")
                .WithDescription(preview)
                .Build();

            var components = new ComponentBuilder()
                .WithButton("Send", $"{CannedYesPrefix}{tag}", ButtonStyle.Success)
                .WithButton("Cancel", CannedNoId, ButtonStyle.Secondary)
                .Build();

            var record = _threadStore.Get(thread.Id);
            var warning = record?.AwaitingCustomer == true
                ? "The customer hasn't replied to your last email. "
                : "";

            await command.ModifyOriginalResponseAsync(p =>
            {
                p.Content = warning + "Send this as a reply to the customer?";
                p.Embed = embed;
                p.Components = components;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Slash command handler failed");
            try
            {
                if (command.HasResponded)
                {
                    await command.ModifyOriginalResponseAsync(p => p.Content = $"Error: {ex.Message}");
                }
                else
                {
                    await command.RespondAsync($"Error: {ex.Message}", ephemeral: true);
                }
            }
            catch
            { 
                // fuck it, go for headshots
            }
        }
    }

    private async Task HandleRefreshEmailAsync(SocketSlashCommand command, double dispatchLagMs)
    {
        var deferStart = DateTimeOffset.UtcNow;
        try
        {
            await command.DeferAsync(ephemeral: true);
        }
        catch (Discord.Net.HttpException dex) when ((int)dex.DiscordCode == 10062)
        {
            var deferElapsedMs = (DateTimeOffset.UtcNow - deferStart).TotalMilliseconds;
            _logger.LogWarning(
                "/refresh-email interaction {Id} expired before defer. " +
                "dispatch_lag={DispatchLagMs:F0}ms, defer_call={DeferElapsedMs:F0}ms",
                command.Id, dispatchLagMs, deferElapsedMs
            );
            return;
        }

        try
        {
            var poller = _services.GetRequiredService<EmailPollingService>();
            var count = await poller.PollAllMailboxesAsync(CancellationToken.None);

            await command.ModifyOriginalResponseAsync(p =>
                p.Content = count == 0
                    ? "No new mail."
                    : $"Fetched {count} new email(s)."
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "/refresh-email handler failed");
            await command.ModifyOriginalResponseAsync(p => p.Content = $"Refresh failed: {ex.Message}");
        }
    }

    private async Task OnButtonAsync(SocketMessageComponent component)
    {
        try
        {
            var id = component.Data.CustomId ?? "";

            if (id.StartsWith(PendingReplyYesPrefix) || id.StartsWith(PendingReplyNoPrefix))
            {
                await HandlePendingReplyButtonAsync(component, id);
                return;
            }

            if (!id.StartsWith("cr:"))
                return;

            if (id == CannedNoId)
            {
                await component.UpdateAsync(p =>
                {
                    p.Content = "Cancelled.";
                    p.Embed = null;
                    p.Components = new ComponentBuilder().Build();
                });
                return;
            }

            if (!id.StartsWith(CannedYesPrefix))
                return;

            var tag = id[CannedYesPrefix.Length..];

            if (component.Channel is not SocketThreadChannel thread || thread.ParentChannel == null)
            {
                await component.UpdateAsync(p =>
                {
                    p.Content = "❌ Not in a thread.";
                    p.Embed = null;
                    p.Components = new ComponentBuilder().Build();
                });
                return;
            }

            var mailbox = _config.Mailboxes.FirstOrDefault(m => m.ForumChannelId == thread.ParentChannel.Id);
            if (mailbox == null)
            {
                await component.UpdateAsync(p =>
                {
                    p.Content = "This thread isn't bound to a configured mailbox.";
                    p.Embed = null;
                    p.Components = new ComponentBuilder().Build();
                });
                return;
            }

            var body = _cannedReplies.Read(tag);
            if (body == null)
            {
                await component.UpdateAsync(p =>
                {
                    p.Content = $"Canned reply `{tag}` no longer exists.";
                    p.Embed = null;
                    p.Components = new ComponentBuilder().Build();
                });
                return;
            }

            await component.DeferAsync(ephemeral: true);

            try
            {
                await SendThreadReplyAsync(thread, mailbox, body, [], CancellationToken.None);

                await thread.SendMessageAsync($"<@{component.User.Id}> sent the **{tag}** canned reply");

                await component.ModifyOriginalResponseAsync(p =>
                {
                    p.Content = $"Sent canned reply **{tag}**.";
                    p.Embed = null;
                    p.Components = new ComponentBuilder().Build();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send canned reply {Tag} for thread {ThreadId}", tag, thread.Id);
                await component.ModifyOriginalResponseAsync(p =>
                {
                    p.Content = $"Failed to send: `{ex.Message}`";
                    p.Embed = null;
                    p.Components = new ComponentBuilder().Build();
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Button handler failed");
        }
    }

    private async Task HandlePendingReplyButtonAsync(SocketMessageComponent component, string id)
    {
        var isYes = id.StartsWith(PendingReplyYesPrefix);
        var token = id[(isYes ? PendingReplyYesPrefix.Length : PendingReplyNoPrefix.Length)..];

        if (!_pendingReplies.TryRemove(token, out var pending))
        {
            await component.UpdateAsync(p =>
            {
                p.Content = "This confirmation has expired or already been handled.";
                p.Components = new ComponentBuilder().Build();
            });
            return;
        }

        if (component.Channel is not SocketThreadChannel thread || thread.Id != pending.ThreadId)
        {
            await component.UpdateAsync(p =>
            {
                p.Content = "Thread context lost.";
                p.Components = new ComponentBuilder().Build();
            });
            return;
        }

        var mailbox = _config.Mailboxes.FirstOrDefault(m => m.ForumChannelId == thread.ParentChannel?.Id);
        if (mailbox == null)
        {
            await component.UpdateAsync(p =>
            {
                p.Content = "This thread isn't bound to a configured mailbox.";
                p.Components = new ComponentBuilder().Build();
            });
            return;
        }

        if (!isYes)
        {
            await component.UpdateAsync(p =>
            {
                p.Content = $"Cancelled by <@{component.User.Id}>.";
                p.Components = new ComponentBuilder().Build();
            });
            return;
        }

        try
        {
            await SendThreadReplyAsync(thread, mailbox, pending.Body, pending.Attachments, CancellationToken.None);
            await component.UpdateAsync(p =>
            {
                p.Content = $"Sent by <@{component.User.Id}>.";
                p.Components = new ComponentBuilder().Build();
            });

            try
            {
                var triggerMsg = await thread.GetMessageAsync(pending.TriggerMessageId);
                if (triggerMsg is IUserMessage userMsg)
                    await userMsg.AddReactionAsync(new Emoji("📧"));
            }
            catch
            {
                // Trigger message may have been deleted; non-fatal.
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send confirmed reply for thread {ThreadId}", thread.Id);
            await component.UpdateAsync(p =>
            {
                p.Content = $"Failed to send: `{ex.Message}`";
                p.Components = new ComponentBuilder().Build();
            });
        }
    }
}
