using EmailToDiscord.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EmailToDiscord.Services;

public enum MatchTarget
{
    Subject,
    Body,
    SubjectAndBody,
}

public enum AutoReplyMode
{
    Suggest,
    Auto,
}

public enum MatchMode
{
    Any,
    All,
}

public record CannedReply(
    string Tag,
    string Body,
    IReadOnlyList<string> Triggers,
    MatchTarget Match,
    AutoReplyMode Mode,
    MatchMode MatchMode,
    bool WholeWord,
    int Priority
);

public class CannedReplyService
{
    private readonly string _root;

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

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

    public string? Read(string tag) => Load(tag)?.Body;

    public CannedReply? Load(string tag)
    {
        if (string.IsNullOrEmpty(tag))
            return null;

        var match = ListTags().FirstOrDefault(t =>
            string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)
        );

        if (match == null)
            return null;

        var path = Path.Combine(_root, match + ".md");
        if (!File.Exists(path))
            return null;

        return Parse(match, File.ReadAllText(path));
    }

    public IReadOnlyList<CannedReply> ListReplies()
    {
        return ListTags()
            .Select(Load)
            .Where(r => r != null)
            .Select(r => r!)
            .ToList();
    }

    private static CannedReply Parse(string tag, string raw)
    {
        var (frontmatter, body) = SplitFrontmatter(raw);

        Frontmatter meta;
        try
        {
            meta = frontmatter != null
                ? YamlDeserializer.Deserialize<Frontmatter>(frontmatter) ?? new Frontmatter()
                : new Frontmatter();
        }
        catch (YamlDotNet.Core.YamlException)
        {
            meta = new Frontmatter();
        }

        var triggers = (meta.Triggers ?? new List<string>())
            .Select(t => t?.Trim() ?? "")
            .Where(t => t.Length > 0)
            .ToList();

        return new CannedReply(
            tag,
            body,
            triggers,
            ParseTarget(meta.Match),
            ParseMode(meta.Mode),
            ParseMatchMode(meta.MatchType),
            meta.WholeWord,
            meta.Priority
        );
    }

    private static (string? Frontmatter, string Body) SplitFrontmatter(string text)
    {
        var normalized = text.TrimStart('\uFEFF');

        using var reader = new StringReader(normalized);
        var first = reader.ReadLine();
        if (first == null || first.Trim() != "---")
            return (null, text.TrimEnd());

        var fmLines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed == "---" || trimmed == "...")
            {
                var body = reader.ReadToEnd() ?? "";
                return (string.Join("\n", fmLines), body.TrimStart('\r', '\n').TrimEnd());
            }
            fmLines.Add(line);
        }

        // No closing delimiter: not valid frontmatter, keep the whole thing as body.
        return (null, text.TrimEnd());
    }

    private static MatchTarget ParseTarget(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "subject" => MatchTarget.Subject,
        "body" => MatchTarget.Body,
        _ => MatchTarget.SubjectAndBody,
    };

    private static AutoReplyMode ParseMode(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "auto" => AutoReplyMode.Auto,
        _ => AutoReplyMode.Suggest,
    };

    private static MatchMode ParseMatchMode(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "all" => MatchMode.All,
        _ => MatchMode.Any,
    };

    private sealed class Frontmatter
    {
        public List<string>? Triggers { get; set; }
        public string? Match { get; set; }
        public string? Mode { get; set; }
        public string? MatchType { get; set; }
        public bool WholeWord { get; set; }
        public int Priority { get; set; }
    }
}
