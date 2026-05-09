using Markdig;
using ReverseMarkdown;

namespace EmailToDiscord.Services;

public class ContentConverter
{
    private readonly Converter _htmlToMarkdown;
    private readonly MarkdownPipeline _markdownPipeline;

    public ContentConverter()
    {
        _htmlToMarkdown = new Converter(new Config
        {
            UnknownTags = Config.UnknownTagsOption.Bypass,
            GithubFlavored = true,
            RemoveComments = true,
            SmartHrefHandling = true,
        });

        _markdownPipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseSoftlineBreakAsHardlineBreak()
            .Build();
    }

    public string HtmlToMarkdown(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        try
        {
            return _htmlToMarkdown.Convert(html).Trim();
        }
        catch
        {
            return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "").Trim();
        }
    }

    public string MarkdownToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";

        return Markdown.ToHtml(markdown, _markdownPipeline);
    }
}
