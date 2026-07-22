namespace EmailToDiscord.Configuration;

public class AppConfig
{
    public DiscordConfig Discord { get; set; } = new();
    public List<MailboxConfig> Mailboxes { get; set; } = new();
    public PollingConfig Polling { get; set; } = new();
    public StateConfig State { get; set; } = new();
    public CannedRepliesConfig CannedReplies { get; set; } = new();
    public AutoresponderConfig Autoresponder { get; set; } = new();
}

public class AutoresponderConfig
{
    public string Footer { get; set; } = "";
}

public class DiscordConfig
{
    public string Token { get; set; } = "";
    public string CommandPrefix { get; set; } = "!reply";
    public List<ulong> GuildIds { get; set; } = new();
}

public class CannedRepliesConfig
{
    public string Path { get; set; } = "./canned-replies/";
}

public class MailboxConfig
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public ulong ForumChannelId { get; set; }
    public ImapConfig Imap { get; set; } = new();
    public SmtpConfig Smtp { get; set; } = new();
    public string TemplatePath { get; set; } = "";
}

public class ImapConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 993;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseSsl { get; set; } = true;
    public string Folder { get; set; } = "INBOX";
}

public class SmtpConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseStarttls { get; set; } = true;
    public string FromName { get; set; } = "";
}

public class PollingConfig
{
    public int IntervalSeconds { get; set; } = 300;
}

public class StateConfig
{
    public string FilePath { get; set; } = "./state.json";
    public string ThreadDbPath { get; set; } = "./threads.db";
}
