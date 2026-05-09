using EmailToDiscord.Configuration;

namespace EmailToDiscord.Services;

public class CannedReplyService
{
    private readonly string _root;

    public CannedReplyService(AppConfig config)
    {
        _root = config.CannedReplies.Path;
    }

    public IReadOnlyList<string> ListTags()
    {
        if (!Directory.Exists(_root))
            return [];
            
        return Directory.GetFiles(_root, "*.md")
            .Select(p => Path.GetFileNameWithoutExtension(p) ?? "")
            .Where(s => !string.IsNullOrEmpty(s))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string? Read(string tag)
    {
        if (string.IsNullOrEmpty(tag))
            return null;

        var match = ListTags().FirstOrDefault(t => 
            string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)
        );

        if (match == null)
            return null;

        var path = Path.Combine(_root, match + ".md");
        return File.Exists(path) ? File.ReadAllText(path).TrimEnd() : null;
    }
}
