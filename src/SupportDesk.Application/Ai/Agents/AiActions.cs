using System.Text.Json;
using System.Text.RegularExpressions;
using SupportDesk.Application.Common;
using SupportDesk.Domain.Ai;

namespace SupportDesk.Application.Ai.Agents;

public sealed record AiActionInfo(string Type, string Label, AiRiskLevel Risk, bool Reversible, bool CustomerFacing, bool Consequential);

/// <summary>
/// Everything an AI agent may propose. The model never executes anything: proposals are validated server-side and either
/// auto-applied (low-risk, reversible, internal changes in AutoLowRisk mode) or wait for a person with ai.approve_actions.
/// Consequential actions (financial commitments, account/data/contract changes) always require approval.
/// </summary>
public static class AiActionCatalog
{
    public const string SetClassification = "set_classification";
    public const string SetPriority = "set_priority";
    public const string AddTags = "add_tags";
    public const string SendReply = "send_reply";
    public const string RequestInfo = "request_info";
    public const string AddNote = "add_note";
    public const string ConsequentialReview = "consequential_review";

    public static readonly IReadOnlyDictionary<string, AiActionInfo> Actions = new Dictionary<string, AiActionInfo>(StringComparer.OrdinalIgnoreCase)
    {
        [SetClassification] = new(SetClassification, "Set category / product / department", AiRiskLevel.Low, true, false, false),
        [SetPriority] = new(SetPriority, "Change priority", AiRiskLevel.Medium, true, false, false),
        [AddTags] = new(AddTags, "Add tags", AiRiskLevel.Low, true, false, false),
        [SendReply] = new(SendReply, "Send reply to customer", AiRiskLevel.Medium, false, true, false),
        [RequestInfo] = new(RequestInfo, "Ask customer for missing information", AiRiskLevel.Low, false, true, false),
        [AddNote] = new(AddNote, "Add internal note", AiRiskLevel.Low, false, false, false),
        [ConsequentialReview] = new(ConsequentialReview, "Human decision required", AiRiskLevel.High, false, false, true)
    };

    public static AiActionInfo Info(string type) => Actions.TryGetValue(type, out var i) ? i : new AiActionInfo(type, type, AiRiskLevel.High, false, false, true);

    /// <summary>Approval policy: anything customer-facing, consequential, not low-risk, uncertain or flagged needs a person.</summary>
    public static bool RequiresApproval(AiActionInfo action, AiRiskLevel risk, AiAgentMode mode, double confidence, double minConfidence, bool injectionFlagged) =>
        action.Consequential || action.CustomerFacing || risk != AiRiskLevel.Low || mode != AiAgentMode.AutoLowRisk || confidence < minConfidence || injectionFlagged;
}

/// <summary>Detects requests that need a human decision (money, contracts, accounts, data changes).</summary>
public static partial class ConsequentialRequestDetector
{
    public static (string Kind, string Excerpt)? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var (kind, regex) in Rules)
        {
            var m = regex.Match(text);
            if (!m.Success) continue;
            var start = Math.Max(0, m.Index - 60);
            var length = Math.Min(text.Length - start, m.Length + 120);
            return (kind, text.Substring(start, length).Replace('\n', ' ').Trim());
        }
        return null;
    }

    private static readonly (string Kind, Regex Pattern)[] Rules =
    [
        ("financial", FinancialRegex()),
        ("contract", ContractRegex()),
        ("account_change", AccountRegex()),
        ("data_change", DataRegex())
    ];

    [GeneratedRegex(@"\b(refund|money back|chargeback|reimburs\w*|credit note|compensation|waive\w*|discount|free of charge|write[- ]off)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FinancialRegex();

    [GeneratedRegex(@"\b(cancel|terminate|end|downgrade|upgrade)\b[^.\n]{0,30}\b(contract|subscription|amc|licen[cs]e|agreement|plan)\b|\brenewal (price|pricing|quote)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ContractRegex();

    [GeneratedRegex(@"\b(delete|close|deactivate|transfer)\b[^.\n]{0,20}\b(my|our|the)?\s*(account|user|login)\b|\bchange\b[^.\n]{0,25}\b(owner|administrator|admin|billing (email|address|contact))\b", RegexOptions.IgnoreCase)]
    private static partial Regex AccountRegex();

    [GeneratedRegex(@"\b(delete|erase|purge|remove)\b[^.\n]{0,25}\b(data|records|personal information|history)\b|\b(gdpr|right to be forgotten)\b|\b(correct|change|modify|alter|reverse)\b[^.\n]{0,25}\b(posted|invoice|ledger|voucher|transaction)s?\b", RegexOptions.IgnoreCase)]
    private static partial Regex DataRegex();
}

/// <summary>Parsed <see cref="AiAgent.ConfigJson"/>.</summary>
public sealed record AgentConfig(
    IReadOnlyList<Guid> Departments,
    IReadOnlyList<string> Channels,
    double? MinConfidence,
    IReadOnlyList<string> AllowedActions,
    int MaxRepliesPerTicket,
    IReadOnlyList<string> RequiredInfo,
    IReadOnlyList<string> Scenarios)
{
    public static AgentConfig Parse(string? json)
    {
        var o = Json.Object(json);
        List<string> L(string k) => o.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList() : [];
        double? min = o.TryGetValue("minConfidence", out var m) && m.ValueKind == JsonValueKind.Number ? Math.Clamp(m.GetDouble(), 0, 1) : null;
        var max = o.TryGetValue("maxRepliesPerTicket", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var n) ? Math.Clamp(n, 0, 5) : 1;
        return new AgentConfig(L("departments").Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList(),
            L("channels"), min, L("allowedActions"), max, L("requiredInfo"), L("scenarios"));
    }

    public bool Allows(string action) => AllowedActions.Count == 0 || AllowedActions.Contains(action, StringComparer.OrdinalIgnoreCase);
}
