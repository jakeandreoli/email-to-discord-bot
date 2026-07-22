namespace EmailToDiscord.Models;

public class IncomingEmail
{
    public string MessageId { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public List<EmailAttachment> Attachments { get; set; } = new();
    public ulong? ReferencedThreadId { get; set; }
    public DateTimeOffset Date { get; set; }
    public bool IsAutomated { get; set; }
}

public class EmailAttachment
{
    public string FileName { get; set; } = "attachment";
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Content { get; set; } = Array.Empty<byte>();
}
