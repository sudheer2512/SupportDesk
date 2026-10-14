using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SupportDesk.Application.Ai;
using SupportDesk.Application.Ai.Safety;
using SupportDesk.Domain.Rules;

namespace SupportDesk.Integrations.Ai.Local;

/// <summary>
/// Extractive summariser: scores sentences by conversation-wide term weight, technical cue words and position, then
/// states the problem, the work so far and the latest update; status and pending actions are derived from ticket state.
/// </summary>
public static partial class ExtractiveSummarizer
{
    public sealed record Summary(string Text, string CurrentStatus, IReadOnlyList<string> PendingActions);

    public static Summary Summarize(TicketSnapshot s)
    {
        var allText = string.Join("\n", new[] { s.Subject, s.Description }.Concat(s.Messages.Select(m => m.Text)));
        var weights = TextNormalization.Tokenize(allText).GroupBy(t => t).ToDictionary(g => g.Key, g => 1 + Math.Log(g.Count()));

        double Score(string sentence, double bonus = 0)
        {
            var tokens = TextNormalization.Tokenize(sentence).Distinct().ToList();
            if (tokens.Count < 3 || Sentences.IsBoilerplate(sentence)) return -1;
            var score = tokens.Sum(t => weights.GetValueOrDefault(t)) / Math.Sqrt(tokens.Count);
            if (CueRegex().IsMatch(sentence)) score += 1.5;
            return score + bonus;
        }

        string? Best(IEnumerable<string> texts, double bonus = 0) => texts.SelectMany(Sentences.Split).Select(x => (x, Score(x, bonus)))
            .Where(x => x.Item2 > 0).OrderByDescending(x => x.Item2).Select(x => x.x).FirstOrDefault();

        var customerTexts = new List<string> { s.Description };
        customerTexts.AddRange(s.Messages.Where(m => m.AuthorType == "Customer").Take(2).Select(m => m.Text));
        var problem = Best(customerTexts) ?? s.Subject;
        var agentMessages = s.Messages.Where(m => m.AuthorType is "Agent" or "System" or "Ai" && m.Kind != "SystemMessage").ToList();
        var progress = Best(agentMessages.Where(m => !m.IsPublic).Select(m => m.Text), 0.5) ?? Best(agentMessages.Select(m => m.Text));
        var last = s.Messages.LastOrDefault();
        var latest = last is null ? null : Best([last.Text]);

        var who = s.Contact ?? (s.Customer is null ? "The customer" : $"{s.Customer}");
        var sb = new StringBuilder();
        sb.Append($"{who}{(s.Contact is not null && s.Customer is not null ? $" ({s.Customer})" : "")} reported: {Sentences.Clean(problem)}");
        if (progress is not null && progress != problem) sb.Append($" Work so far: {Sentences.Clean(progress)}");
        else if (agentMessages.Count == 0) sb.Append(" No agent has responded yet.");
        if (last is not null && latest is not null && latest != problem && latest != progress)
            sb.Append($" Latest ({(last.AuthorType == "Customer" ? "customer" : last.AuthorName ?? "agent")}, {Sentences.Ago(last.CreatedAt, s.Now)}): {Sentences.Clean(latest)}");

        return new Summary(sb.ToString(), Status(s), Actions(s));
    }

    private static string Status(TicketSnapshot s)
    {
        var parts = new List<string> { $"{s.Status}", s.Assignee is null ? "unassigned" : $"assigned to {s.Assignee}", $"{s.Priority} priority" };
        if (s.AwaitingCustomerConfirmation) parts.Add("awaiting customer confirmation");
        switch (s.SlaState)
        {
            case "Breached": parts.Add("SLA breached"); break;
            case "AtRisk": parts.Add("SLA at risk"); break;
            case "Paused": parts.Add("SLA paused"); break;
        }
        if (s.StatusCategory is not ("Resolved" or "Closed") && s.DueAt is { } due)
            parts.Add(due > s.Now ? $"resolution due in {Sentences.In(due, s.Now)}" : $"resolution overdue by {Sentences.In(s.Now, due)}");
        if (s.StatusCategory == "Waiting" && s.Messages.LastOrDefault(m => m.AuthorType != "Customer" && m.IsPublic) is { } ask)
            parts.Add($"waiting on the customer since {Sentences.Ago(ask.CreatedAt, s.Now)}");
        return string.Join(" · ", parts) + ".";
    }

    private static List<string> Actions(TicketSnapshot s)
    {
        var actions = new List<string>();
        var open = s.StatusCategory is not ("Resolved" or "Closed");
        var publicMessages = s.Messages.Where(m => m.IsPublic).ToList();
        var lastPublic = publicMessages.LastOrDefault();
        if (open && s.Assignee is null) actions.Add("Assign an owner to the ticket.");
        if (open && lastPublic is { AuthorType: "Customer" })
            actions.Add($"Reply to {(s.ContactFirstName ?? "the customer")}'s latest message (received {Sentences.Ago(lastPublic.CreatedAt, s.Now)}).");
        else if (open && publicMessages.Count == 0 && s.Messages.All(m => m.AuthorType == "Customer"))
            actions.Add("Send the first response to the customer.");
        if (open && lastPublic is { AuthorType: not "Customer" } && QuestionRegex().Match(lastPublic.Text) is { Success: true } q)
            actions.Add($"Wait for the customer to answer: \"{TextNormalization.Truncate(q.Value.Trim(), 140)}\"" + (s.StatusCategory == "Waiting" ? " — follow up if there is no reply soon." : ""));
        if (open && s.SlaState == "Breached") actions.Add("The SLA is breached: prioritise this ticket and update the customer.");
        else if (open && s.SlaState == "AtRisk") actions.Add("The SLA is at risk: respond or resolve before the due time.");
        actions.AddRange(s.OpenTasks.Take(3).Select(t => $"Complete task: {t}."));
        if (s.AwaitingCustomerConfirmation) actions.Add("Await the customer's confirmation; close the ticket if there is no response.");
        if (open && s.Messages.Any(m => !m.IsPublic && EngineeringRegex().IsMatch(m.Text))) actions.Add("Follow up with engineering on the escalated issue.");
        if (actions.Count == 0) actions.Add(open ? "Continue the investigation and keep the customer informed." : "No pending actions.");
        return actions.Distinct().Take(6).ToList();
    }

    [GeneratedRegex(@"\b(error|fail\w*|unable|cannot|can't|not working|mismatch|timeout|time[sd]? out|crash\w*|freez\w*|root cause|fixed|resolved|workaround|reproduc\w*|configur\w*|index\w*|upgrade|version|blocking|wrong|missing|duplicate)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CueRegex();
    [GeneratedRegex(@"[^.!?\n]{8,200}\?")] private static partial Regex QuestionRegex();
    [GeneratedRegex(@"\b(engineering|escalat\w*|defect|bug|developer)\b", RegexOptions.IgnoreCase)] private static partial Regex EngineeringRegex();
}

/// <summary>
/// Template-based grounded composer: quotes the most relevant steps of retrieved knowledge with numbered citations, adds
/// how a similar ticket was resolved (agent-facing drafts only) and asks for missing information when nothing applies.
/// </summary>
public static partial class GroundedComposer
{
    public sealed record Composition(string Body, IReadOnlyList<int> Citations, double Confidence, bool Grounded);

    public static Composition ComposeReply(ReplyTask task)
    {
        var s = task.Ticket;
        var query = $"{s.Subject}\n{LatestCustomerText(s)}";
        var queryTokens = TextNormalization.Tokenize(query).ToHashSet();
        var usable = task.Sources.Where(x => x.Score >= 0.2 && (!task.CustomerFacingOnly || (x.SourceType == "Article" && x.Visibility != "Internal"))).ToList();
        var articles = usable.Where(x => x.SourceType == "Article").Take(2).ToList();
        var tickets = task.CustomerFacingOnly ? [] : usable.Where(x => x.SourceType == "Ticket").Take(1).ToList();
        var sb = new StringBuilder();
        sb.Append(s.ContactFirstName is { Length: > 0 } name ? $"Hi {name},\n\n" : "Hello,\n\n");
        sb.Append($"Thank you for contacting us about \"{s.Subject}\".\n\n");
        var cited = new List<int>();
        foreach (var a in articles)
        {
            var steps = RelevantLines(a.Text, queryTokens, 6);
            if (steps.Count == 0) continue;
            sb.Append(cited.Count == 0 ? $"Our help article \"{a.Title}\" [{a.Index}] explains how to solve this:\n" : $"See also \"{a.Title}\" [{a.Index}]:\n");
            foreach (var step in steps) sb.Append("- ").Append(step).Append('\n');
            sb.Append('\n');
            cited.Add(a.Index);
        }
        foreach (var t in tickets)
        {
            var line = RelevantLines(t.Text.Replace("Resolution:", "\nResolution:"), queryTokens, 2, preferPrefix: "Resolution:")
                .Select(l => l.Replace("Resolution:", "").Trim()).FirstOrDefault(l => l.Length > 0);
            if (line is null) continue;
            sb.Append($"In a similar case, the issue was resolved as follows: {Sentences.Clean(line)} [{t.Index}]\n\n");
            cited.Add(t.Index);
        }
        if (cited.Count == 0)
        {
            sb.Append("We are looking into this. To help us investigate quickly, could you please share the product version, whether this happens in " +
                      "Production or a test environment, the steps that lead to the problem and the exact error message (a screenshot helps)?\n\n");
        }
        else
        {
            sb.Append("If this does not solve the problem, reply to this message with any error details and we will continue from there.\n\n");
        }
        // Agent instructions steer remote models; the template composer keeps its fixed, reviewed wording.
        sb.Append("Regards,\n").Append(task.AgentName ?? "Support team");

        var top = usable.Where(u => cited.Contains(u.Index)).Select(u => u.Score).DefaultIfEmpty(0).Max();
        var titleOverlap = usable.Where(u => cited.Contains(u.Index)).Select(u => TitleOverlap(u.Title, queryTokens)).DefaultIfEmpty(0).Max();
        var confidence = cited.Count == 0 ? 0.2 : Math.Round(Math.Clamp(0.35 + 0.5 * top + 0.3 * titleOverlap, 0, 0.95), 3);
        return new Composition(sb.ToString(), cited, confidence, cited.Count > 0);
    }

    public static Composition ComposeAnswer(AssistantTask task)
    {
        var queryTokens = TextNormalization.Tokenize(task.Question).ToHashSet();
        var usable = task.Sources.Where(x => x.Score >= 0.2 && x.Visibility != "Internal").Take(2).ToList();
        if (usable.Count == 0)
            return new Composition("I couldn't find an answer to that in our help center. I can connect you with a support agent who will help.", [], 0.15, false);
        var sb = new StringBuilder("Here is what I found in our help center:\n\n");
        var cited = new List<int>();
        foreach (var a in usable)
        {
            var steps = RelevantLines(a.Text, queryTokens, 5);
            if (steps.Count == 0) continue;
            sb.Append($"{a.Title} [{a.Index}]\n");
            foreach (var step in steps) sb.Append("- ").Append(step).Append('\n');
            sb.Append('\n');
            cited.Add(a.Index);
        }
        sb.Append("Did this answer your question? If not, I can connect you with a support agent.");
        var top = usable.Where(u => cited.Contains(u.Index)).Select(u => u.Score).DefaultIfEmpty(0).Max();
        var titleOverlap = usable.Where(u => cited.Contains(u.Index)).Select(u => TitleOverlap(u.Title, queryTokens)).DefaultIfEmpty(0).Max();
        var confidence = cited.Count == 0 ? 0.15 : Math.Round(Math.Clamp(0.3 + 0.5 * top + 0.3 * titleOverlap, 0, 0.95), 3);
        return new Composition(sb.ToString(), cited, confidence, cited.Count > 0);
    }

    private static double TitleOverlap(string title, IReadOnlySet<string> queryTokens)
    {
        var t = TextNormalization.Tokenize(title).Distinct().ToList();
        return t.Count == 0 ? 0 : (double)t.Count(queryTokens.Contains) / t.Count;
    }

    private static string LatestCustomerText(TicketSnapshot s) =>
        s.Messages.LastOrDefault(m => m.AuthorType == "Customer")?.Text is { Length: > 0 } last ? last : s.Description;

    /// <summary>Lines that look like steps or overlap the question, in document order.</summary>
    private static List<string> RelevantLines(string text, IReadOnlySet<string> queryTokens, int max, string? preferPrefix = null)
    {
        var lines = Sentences.Split(text);
        var scored = lines.Select((l, i) =>
        {
            var tokens = TextNormalization.Tokenize(l).ToHashSet();
            var overlap = tokens.Count(queryTokens.Contains);
            var score = overlap + (StepRegex().IsMatch(l) ? 1.5 : 0) + (preferPrefix is not null && l.StartsWith(preferPrefix, StringComparison.OrdinalIgnoreCase) ? 3 : 0);
            return (Line: l, Index: i, Score: score);
        }).Where(x => x.Score > 0.9 && !Sentences.IsBoilerplate(x.Line) && !x.Line.StartsWith("Keywords:", StringComparison.OrdinalIgnoreCase)).ToList();
        return scored.OrderByDescending(x => x.Score).Take(max).OrderBy(x => x.Index).Select(x => Sentences.Clean(x.Line)).ToList();
    }

    [GeneratedRegex(@"^\s*(\d+[.)]\s|step\b)|\b(click|open|go to|select|enter|choose|set|enable|disable|restart|sign in|sign out|navigate|upgrade|re-?save|export|check|run|update)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StepRegex();
}

/// <summary>Detects missing troubleshooting information (version, environment, steps, error message, screenshot, impact).</summary>
public static partial class InfoCollector
{
    public static (List<string> Missing, string Request) Analyze(InfoCollectorTask task)
    {
        var s = task.Ticket;
        var text = string.Join("\n", new[] { s.Subject, s.Description }.Concat(s.Messages.Where(m => m.AuthorType == "Customer").Select(m => m.Text)));
        var missing = new List<string>();
        foreach (var item in task.RequiredInfo)
        {
            var present = item switch
            {
                InfoItems.Version => s.HasVersionField || VersionRegex().IsMatch(text),
                InfoItems.Environment => s.CustomFields.ContainsKey("environment") || EnvironmentRegex().IsMatch(text),
                InfoItems.Steps => StepsRegex().IsMatch(text),
                InfoItems.ErrorMessage => ErrorRegex().IsMatch(text),
                InfoItems.Screenshot => s.HasAttachments,
                InfoItems.ImpactScope => ImpactRegex().IsMatch(text) || s.CustomFields.ContainsKey("affected_users") || s.CustomFields.ContainsKey("business_impact"),
                _ => true
            };
            if (!present) missing.Add(item);
        }
        if (missing.Count == 0) return (missing, "");
        var lines = missing.Select(m => m switch
        {
            InfoItems.Version => "the product version you are using (Help › About)",
            InfoItems.Environment => "whether this happens in Production, UAT or a test system",
            InfoItems.Steps => "the steps that lead to the problem",
            InfoItems.ErrorMessage => "the exact error message you see",
            InfoItems.Screenshot => "a screenshot of the screen or error",
            _ => "how many users or locations are affected"
        });
        var request = $"{(s.ContactFirstName is { Length: > 0 } n ? $"Hi {n}," : "Hello,")}\n\nThank you for reporting this. To investigate quickly, could you please share:\n" +
                      string.Join("\n", lines.Select(l => "- " + l)) + "\n\nThank you!";
        return (missing, request);
    }

    [GeneratedRegex(@"\b(v(ersion)?\s*\.?\s*\d+(\.\d+)+|\d+\.\d+(\.\d+)?\s*(version|build)|build\s*\d+|release\s*\d+)\b|\bversion\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
    [GeneratedRegex(@"\b(production|prod|live|uat|staging|test (system|server|environment)|sandbox|qa)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnvironmentRegex();
    [GeneratedRegex(@"\b(steps?|when i|when we|after (i|we)|click\w*|open\w*|go to|tried|reproduce|while (posting|saving|creating|exporting|printing)|scan\w*|export\w*|post\w*|sav(e|ing))\b|^\s*\d+[.)]", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex StepsRegex();
    [GeneratedRegex(@"\b(error|exception|code\s*\d+|failed with|message says|says ['""]|invalid|timeout|time[sd] out|freez\w*|crash\w*)\b|['""][^'""]{6,}['""]", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorRegex();
    [GeneratedRegex(@"\b(all users|\d+\s*(users|people|branches|stores|locations|employees)|everyone|whole team|nobody can|no one can)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImpactRegex();
}

/// <summary>Root causes ranked by similarity-weighted votes of similar resolved tickets.</summary>
public static class RootCauseRanker
{
    public sealed record Cause(string Label, string? Code, double Likelihood, string Explanation, IReadOnlyList<Guid> Evidence);

    public static List<Cause> Rank(RootCauseTask task)
    {
        var withCause = task.Evidence.Where(e => e.RootCauseCode is not null || e.RootCauseLabel is not null).ToList();
        if (withCause.Count == 0)
            return task.Evidence.Count == 0 ? [] :
            [
                new Cause("Not determined — review the similar resolved tickets", null, 0.2,
                    $"{task.Evidence.Count} similar resolved ticket(s) exist but none recorded a root cause.", task.Evidence.Take(3).Select(e => e.TicketId).ToList())
            ];
        var total = withCause.Sum(e => Math.Max(0.05, e.Similarity));
        return withCause.GroupBy(e => e.RootCauseCode ?? e.RootCauseLabel!)
            .Select(g =>
            {
                var weight = g.Sum(e => Math.Max(0.05, e.Similarity));
                var examples = g.OrderByDescending(e => e.Similarity).Take(3).ToList();
                var label = g.First().RootCauseLabel ?? g.Key;
                var explanation = $"Recorded on {g.Count()} similar resolved ticket(s), e.g. #{examples[0].Number} \"{TextNormalization.Truncate(examples[0].Subject, 80)}\"" +
                                  (examples[0].Resolution is { Length: > 0 } r ? $" — {TextNormalization.Truncate(r, 120)}" : ".");
                return new Cause(label, g.First().RootCauseCode, Math.Round(weight / total, 3), explanation, examples.Select(e => e.TicketId).ToList());
            })
            .OrderByDescending(c => c.Likelihood).Take(3).ToList();
    }
}

/// <summary>Troubleshooting article draft from a ticket (problem, environment, cause, resolution steps), with personal data removed.</summary>
public static class KnowledgeDrafter
{
    public static (string Title, string Summary, string BodyHtml) Draft(KnowledgeDraftTask task)
    {
        var s = task.Ticket;
        string Scrub(string text)
        {
            var redaction = new PiiRedaction();
            var t = PiiRedactor.Redact(text, redaction);
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\[(EMAIL|PHONE|CARD|AADHAAR|PAN)_\d+\]", "[redacted]");
            foreach (var name in new[] { s.Contact, s.ContactFirstName, s.Customer }.Where(n => !string.IsNullOrWhiteSpace(n) && n!.Length >= 3))
                t = t.Replace(name!, "the customer", StringComparison.OrdinalIgnoreCase);
            return t;
        }
        var subject = System.Text.RegularExpressions.Regex.Replace(s.Subject, @"^(re|fw|fwd)\s*:\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        var problem = Scrub(s.Description.Length > 0 ? s.Description : subject);
        var resolutionSource = !string.IsNullOrWhiteSpace(s.ResolutionText) ? s.ResolutionText
            : string.Join("\n", s.Messages.Where(m => m.AuthorType == "Agent").TakeLast(3).Select(m => m.Text));
        var steps = Sentences.Split(Scrub(resolutionSource ?? "")).Where(x => !Sentences.IsBoilerplate(x)).Take(8).ToList();
        var notes = s.Messages.Where(m => !m.IsPublic).Select(m => Scrub(m.Text)).Where(t => t.Length > 0).TakeLast(2).ToList();
        string E(string x) => WebUtility.HtmlEncode(x);
        var body = new StringBuilder();
        body.Append("<h2>Problem</h2><p>").Append(E(TextNormalization.Truncate(problem, 1200))).Append("</p>");
        body.Append("<h2>Environment</h2><ul>");
        body.Append("<li>Product: ").Append(E(s.Product ?? "Not specified")).Append(s.Version is null ? "" : " " + E(s.Version)).Append("</li>");
        if (s.Category is not null) body.Append("<li>Area: ").Append(E(s.Category)).Append(s.Subcategory is null ? "" : " › " + E(s.Subcategory)).Append("</li>");
        body.Append("</ul>");
        body.Append("<h2>Cause</h2><p>").Append(E(s.RootCause ?? "Not recorded on the ticket — confirm before publishing.")).Append("</p>");
        body.Append("<h2>Resolution</h2>");
        if (steps.Count > 0)
        {
            body.Append("<ol>");
            foreach (var step in steps) body.Append("<li>").Append(E(Sentences.Clean(step))).Append("</li>");
            body.Append("</ol>");
        }
        else body.Append("<p>Describe the fix here.</p>");
        if (notes.Count > 0)
        {
            body.Append("<h3>Technical notes</h3><ul>");
            foreach (var n in notes) body.Append("<li>").Append(E(TextNormalization.Truncate(n, 400))).Append("</li>");
            body.Append("</ul>");
        }
        body.Append("<p><em>Drafted from ticket ").Append(E(s.DisplayNumber)).Append(". Review the wording and remove customer-specific details before publishing.</em></p>");
        var summary = Sentences.Split(problem).FirstOrDefault() ?? subject;
        return ($"How to resolve: {TextNormalization.Truncate(subject, 150)}", TextNormalization.Truncate(Sentences.Clean(summary), 300), body.ToString());
    }
}
