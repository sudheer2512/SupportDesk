using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Ai.Analytics;
using SupportDesk.Domain;
using SupportDesk.Persistence;

namespace SupportDesk.Application.Ai;

public sealed record PromptDefinition(string Key, string SystemPrompt, string UserTemplate, IReadOnlyList<string> Placeholders);

public sealed record EffectivePrompt(string Key, string SystemPrompt, string UserTemplate, int Version, bool IsOverride);

/// <summary>Prompt template keys (a feature may use several, e.g. agents use classification and info_collector).</summary>
public static class PromptKeys
{
    public const string Classification = "classification";
    public const string Summarization = "summarization";
    public const string ReplySuggestion = "reply_suggestion";
    public const string Sentiment = "sentiment";
    public const string RootCause = "root_cause";
    public const string KnowledgeGeneration = "knowledge_generation";
    public const string Translation = "translation";
    public const string NlAnalytics = "nl_analytics";
    public const string PortalAssistant = "portal_assistant";
    public const string InfoCollector = "info_collector";
    public const string ProviderTest = "provider_test";
}

/// <summary>
/// Built-in, versioned (version 0) prompt templates. Tenants may override any of them (AiPromptTemplate rows with
/// increasing versions); the security preamble and data-block wrapping are always added by the gateway and cannot be
/// removed by an override.
/// </summary>
public static class AiPromptCatalog
{
    private static string Join(IEnumerable<string> items) => string.Join(", ", items);

    public static readonly IReadOnlyDictionary<string, PromptDefinition> Defaults = new Dictionary<string, PromptDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        [PromptKeys.Classification] = new(PromptKeys.Classification,
            "You are the triage assistant of a customer support help desk. Classify the ticket using ONLY the option values listed for each field. " +
            "Respond with one JSON object: {\"fields\":{\"category\":{\"value\":\"<option value or null>\",\"confidence\":0.0-1.0}," +
            "\"product\":{...},\"priority\":{...},\"department\":{...}},\"tags\":[\"existing tag names that apply\"],\"explanation\":\"one sentence\"}. " +
            "Use null when no option fits. Confidence must reflect how certain you are.",
            "Options per field (value = label):\n{{options}}\n\nKnown tags: {{tags}}\n\nTicket to classify:\n{{ticket}}",
            ["options", "tags", "ticket"]),

        [PromptKeys.Summarization] = new(PromptKeys.Summarization,
            "You summarise customer support tickets for support agents. Respond with one JSON object: {\"summary\":\"2-4 sentences: the problem, " +
            "what has been done so far and the latest update\",\"currentStatus\":\"one sentence\",\"pendingActions\":[\"short imperative items, who should do what\"]}. " +
            "Be factual and concise; never invent details that are not in the ticket.",
            "Ticket metadata:\n{{metadata}}\n\nConversation (oldest first):\n{{conversation}}",
            ["metadata", "conversation"]),

        [PromptKeys.ReplySuggestion] = new(PromptKeys.ReplySuggestion,
            "You draft replies that support agents review before sending. Ground every factual statement in the numbered sources and cite them " +
            "inline as [n]. If the sources do not answer the customer's question, ask for the information needed instead of guessing. Never promise " +
            "refunds, credits, discounts, contract or account changes. Respond with one JSON object: {\"body\":\"plain-text reply with [n] citations\"," +
            "\"citations\":[n,...],\"confidence\":0.0-1.0,\"grounded\":true|false}.",
            "Agent instructions: {{instructions}}\nAgent name: {{agent}}\n\nTicket:\n{{ticket}}\n\nSources:\n{{sources}}",
            ["instructions", "agent", "ticket", "sources"]),

        [PromptKeys.Sentiment] = new(PromptKeys.Sentiment,
            "Classify the sentiment of a customer's support message. Respond with one JSON object: {\"sentiment\":\"Positive|Neutral|Negative|Frustrated\"," +
            "\"score\":-1.0..1.0,\"frustrated\":true|false,\"cues\":[\"short phrases from the message\"]}. Frustrated means repeated problems, " +
            "impatience, anger or threats to escalate or leave.",
            "Customer message:\n{{message}}",
            ["message"]),

        [PromptKeys.RootCause] = new(PromptKeys.RootCause,
            "You help support engineers identify likely root causes. Use only the evidence tickets provided. Respond with one JSON object: " +
            "{\"causes\":[{\"cause\":\"short label\",\"code\":\"root cause code taken from the evidence or null\",\"likelihood\":0.0-1.0," +
            "\"explanation\":\"one sentence\",\"evidence\":[\"evidence ticket id\",...]}]} with at most 3 causes ordered by likelihood.",
            "Current ticket:\n{{ticket}}\n\nEvidence (similar resolved tickets):\n{{evidence}}",
            ["ticket", "evidence"]),

        [PromptKeys.KnowledgeGeneration] = new(PromptKeys.KnowledgeGeneration,
            "You write troubleshooting knowledge-base articles from resolved support tickets. Remove personal data and customer names. Respond with " +
            "one JSON object: {\"title\":\"...\",\"summary\":\"one sentence\",\"bodyHtml\":\"<h2>Problem</h2><p>…</p><h2>Cause</h2><p>…</p>" +
            "<h2>Resolution</h2><ol><li>…</li></ol>\"}. Use only <h2>, <h3>, <p>, <ol>, <ul>, <li>, <strong>, <em> and <code> tags.",
            "Resolved ticket:\n{{ticket}}",
            ["ticket"]),

        [PromptKeys.Translation] = new(PromptKeys.Translation,
            "Translate the text into the target language. Preserve meaning, tone, line breaks, product names, numbers and placeholders such as " +
            "[EMAIL_1]. Respond with one JSON object: {\"text\":\"translated text\"}.",
            "Target language: {{target_locale}}\nSource language: {{source_locale}}\n\nText:\n{{text}}",
            ["target_locale", "source_locale", "text"]),

        [PromptKeys.NlAnalytics] = new(PromptKeys.NlAnalytics,
            "You convert help-desk analytics questions into a structured query. Never write SQL. Respond with one JSON object: " +
            "{\"metric\":\"" + Join(AnalyticsCatalog.Metrics.Keys) + "\",\"dimension\":\"" + Join(AnalyticsCatalog.Dimensions.Keys) + "\"," +
            "\"filters\":[{\"field\":\"" + Join(AnalyticsCatalog.FilterFields) + "\",\"value\":\"the vocabulary value (an id) or an enum name\"}]," +
            "\"period\":\"" + Join(AnalyticsCatalog.Periods) + "\",\"sort\":\"asc|desc\",\"limit\":1-50}. Choose exactly one value for metric, " +
            "dimension and period from the lists. Priority values: Low, Medium, High, Critical. Channel values: Email, Portal, WebForm, Chat, " +
            "WhatsApp, Phone, Sms, Facebook, Instagram, MobileApp, Api, WalkIn, Erp, Internal.",
            "Vocabulary (field | value | label):\n{{vocabulary}}\n\nToday (UTC): {{today}}\n\nQuestion:\n{{question}}",
            ["vocabulary", "today", "question"]),

        [PromptKeys.PortalAssistant] = new(PromptKeys.PortalAssistant,
            "You are the self-service assistant of a customer help center. Answer only from the numbered help-center sources and cite them as [n]. " +
            "If the sources do not contain the answer, or the customer asks for a person, set handoff to true and offer to connect them with a " +
            "support agent. Never promise refunds, credits, discounts or account changes. Respond with one JSON object: {\"answer\":\"...\"," +
            "\"citations\":[n],\"confidence\":0.0-1.0,\"handoff\":true|false}.",
            "Preferred language: {{locale}}\n\nSources:\n{{sources}}\n\nCustomer question:\n{{question}}",
            ["locale", "sources", "question"]),

        [PromptKeys.InfoCollector] = new(PromptKeys.InfoCollector,
            "You check whether a support ticket contains the information an engineer needs to start troubleshooting. Possible items: " +
            Join(InfoItems.All) + ". Respond with one JSON object: {\"missing\":[\"item\",...],\"request\":\"a short, polite message to the customer " +
            "asking only for the missing items\"}. Return an empty list when nothing required is missing, and for how-to questions, billing or " +
            "commercial requests that need no troubleshooting.",
            "Required items: {{required}}\n\nTicket:\n{{ticket}}",
            ["required", "ticket"]),

        [PromptKeys.ProviderTest] = new(PromptKeys.ProviderTest,
            "You are answering a connectivity test from a help-desk administrator. Reply briefly.",
            "{{prompt}}",
            ["prompt"])
    };
}

/// <summary>Resolves the effective prompt template (tenant override or built-in default) and renders placeholders.</summary>
public sealed partial class AiPromptService(DeskDbContext db)
{
    public async Task<EffectivePrompt> GetEffectiveAsync(string key, CancellationToken ct)
    {
        var def = AiPromptCatalog.Defaults.TryGetValue(key, out var d) ? d
            : throw new DomainException("ai_prompt_missing", $"No prompt template exists for '{key}'.", 500);
        var custom = await db.AiPromptTemplates.AsNoTracking().Where(t => t.Feature == def.Key && t.IsActive)
            .OrderByDescending(t => t.Version).FirstOrDefaultAsync(ct);
        return custom is null
            ? new EffectivePrompt(def.Key, def.SystemPrompt, def.UserTemplate, 0, false)
            : new EffectivePrompt(def.Key, custom.SystemPrompt, custom.UserTemplate, custom.Version, true);
    }

    /// <summary>Replaces {{name}} placeholders. Unknown placeholders render empty; values are inserted verbatim (already wrapped when untrusted).</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        PlaceholderRegex().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

    public static IReadOnlyList<string> PlaceholdersOf(string template) =>
        PlaceholderRegex().Matches(template).Select(m => m.Groups[1].Value).Distinct().ToList();

    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_]+)\s*\}\}")] private static partial Regex PlaceholderRegex();
}
