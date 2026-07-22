using System.Text.RegularExpressions;
using EmailToDiscord.Models;
using Microsoft.Extensions.Logging;

namespace EmailToDiscord.Services;

public class AutoresponderService
{
    private readonly CannedReplyService _cannedReplies;
    private readonly ILogger<AutoresponderService> _logger;

    public AutoresponderService(CannedReplyService cannedReplies, ILogger<AutoresponderService> logger)
    {
        _cannedReplies = cannedReplies;
        _logger = logger;
    }

    public CannedReply? Match(IncomingEmail email)
    {
        CannedReply? best = null;
        int bestMatched = 0;

        foreach (var reply in _cannedReplies.ListReplies())
        {
            if (reply.Triggers.Count == 0)
                continue;

            var haystack = BuildHaystack(reply.Match, email);
            if (string.IsNullOrWhiteSpace(haystack))
                continue;

            int matched = CountMatches(reply, haystack);

            bool qualifies = reply.MatchMode == MatchMode.All
                ? matched == reply.Triggers.Count
                : matched > 0;

            if (!qualifies)
                continue;

            if (best == null
                || reply.Priority > best.Priority
                || (reply.Priority == best.Priority && matched > bestMatched)
                || (reply.Priority == best.Priority && matched == bestMatched && string.Compare(reply.Tag, best.Tag, StringComparison.OrdinalIgnoreCase) < 0)
            )
            {
                best = reply;
                bestMatched = matched;
            }
        }

        if (best != null)
        {
            _logger.LogInformation(
                "Autoresponder matched canned reply {Tag} (mode {Mode}) for mail from {From} / subject {Subject}",
                best.Tag, best.Mode, email.FromAddress, email.Subject
            );
        }

        return best;
    }

    private static string BuildHaystack(MatchTarget target, IncomingEmail email) => target switch
    {
        MatchTarget.Subject => email.Subject ?? "",
        MatchTarget.Body => email.Body ?? "",
        _ => $"{email.Subject}\n{email.Body}",
    };

    private static int CountMatches(CannedReply reply, string haystack)
    {
        int matched = 0;
        foreach (var trigger in reply.Triggers)
        {
            if (ContainsTrigger(haystack, trigger, reply.WholeWord))
                matched++;
        }
        return matched;
    }

    private static bool ContainsTrigger(string haystack, string trigger, bool wholeWord)
    {
        if (!wholeWord)
            return haystack.Contains(trigger, StringComparison.OrdinalIgnoreCase);

        var pattern = $@"(?<!\w){Regex.Escape(trigger)}(?!\w)";
        return Regex.IsMatch(haystack, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
