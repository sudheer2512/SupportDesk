using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Automation;
using SupportDesk.Domain.Automation;
using SupportDesk.Domain.Rules;

namespace SupportDesk.Application.Automation;

public sealed record ActionParamSpec(string Name, string Label, string Type, bool Required = false, string? Description = null, IReadOnlyList<string>? Options = null);

public sealed record ActionSpec(string Type, string Label, string Category, string Description, IReadOnlyList<ActionParamSpec> Params);

/// <summary>Static catalog of triggers, operators and action types (with parameter schemas) for the designer and validation.</summary>
public static class AutomationCatalog
{
    public static readonly string[] Priorities = ["Low", "Medium", "High", "Critical"];
    public static readonly string[] Severities = ["Cosmetic", "Minor", "Major", "Critical"];
    public static readonly string[] MessageChannels = ["Email", "Sms", "WhatsApp"];
    public static readonly string[] HttpMethods = ["GET", "POST", "PUT", "PATCH", "DELETE"];
    public static readonly string[] NotifyRecipients = ["assignee", "supervisors", "watchers", "team", "department"];

    /// <summary>Triggers fired by outbox events (the event type equals the trigger key).</summary>
    public static readonly HashSet<string> EventTriggers =
    [
        RuleTriggers.TicketCreated, RuleTriggers.TicketUpdated, RuleTriggers.StatusChanged, RuleTriggers.TicketAssigned, RuleTriggers.ReplyAdded,
        RuleTriggers.CustomerReplied, RuleTriggers.NoteAdded, RuleTriggers.SlaWarning, RuleTriggers.SlaBreached, RuleTriggers.CustomerActivity
    ];

    public static readonly IReadOnlyList<TriggerInfoDto> Triggers =
    [
        new(RuleTriggers.TicketCreated, "Ticket created", "event", "A ticket is created from any channel."),
        new(RuleTriggers.TicketUpdated, "Ticket updated", "event", "Ticket fields change (optionally only when specific fields change)."),
        new(RuleTriggers.StatusChanged, "Status changed", "event", "The ticket status changes (use 'changed_to'/'changed_from' on status)."),
        new(RuleTriggers.TicketAssigned, "Ticket assigned", "event", "The assignee or team changes."),
        new(RuleTriggers.ReplyAdded, "Agent replied", "event", "An agent sends a public reply."),
        new(RuleTriggers.CustomerReplied, "Customer replied", "event", "The customer replies through any channel."),
        new(RuleTriggers.NoteAdded, "Note added", "event", "An internal or handover note is added."),
        new(RuleTriggers.SlaWarning, "SLA warning", "event", "An SLA milestone reaches its warning threshold."),
        new(RuleTriggers.SlaBreached, "SLA breached", "event", "An SLA milestone is breached."),
        new(RuleTriggers.CustomerActivity, "Customer activity", "event", "Activity on a customer account (rules run on the related or open tickets)."),
        new(RuleTriggers.Scheduled, "Scheduled", "scheduled", "Evaluated on a schedule over open tickets (time-based conditions such as 'older_than_minutes')."),
        new(RuleTriggers.Webhook, "Inbound webhook", "webhook", "An external system posts to the rule's webhook URL with a ticket id or number.")
    ];

    public static readonly IReadOnlyList<OperatorInfoDto> Operators =
    [
        new(ConditionOps.Equals, "is", "value", ["text", "enum", "priority", "number", "bool", "user", "team", "department", "status", "category", "customer", "contact", "product", "module", "version", "installation", "issueType", "brand", "tags"]),
        new(ConditionOps.NotEquals, "is not", "value", ["text", "enum", "priority", "number", "bool", "user", "team", "department", "status", "category", "customer", "contact", "product", "module", "version", "installation", "issueType", "brand", "tags"]),
        new(ConditionOps.In, "is any of", "list", ["text", "enum", "priority", "number", "user", "team", "department", "status", "category", "customer", "contact", "product", "module", "version", "installation", "issueType", "brand", "tags"]),
        new(ConditionOps.NotIn, "is none of", "list", ["text", "enum", "priority", "number", "user", "team", "department", "status", "category", "customer", "contact", "product", "module", "version", "installation", "issueType", "brand", "tags"]),
        new(ConditionOps.Contains, "contains", "value", ["text", "tags"]),
        new(ConditionOps.NotContains, "does not contain", "value", ["text", "tags"]),
        new(ConditionOps.ContainsAny, "contains any of", "list", ["text", "tags"]),
        new(ConditionOps.StartsWith, "starts with", "value", ["text"]),
        new(ConditionOps.EndsWith, "ends with", "value", ["text"]),
        new(ConditionOps.Matches, "matches pattern", "regex", ["text"]),
        new(ConditionOps.IsEmpty, "is empty", "none", ["text", "enum", "user", "team", "category", "customer", "contact", "product", "module", "version", "installation", "issueType", "datetime", "tags", "number"]),
        new(ConditionOps.IsNotEmpty, "is not empty", "none", ["text", "enum", "user", "team", "category", "customer", "contact", "product", "module", "version", "installation", "issueType", "datetime", "tags", "number"]),
        new(ConditionOps.GreaterThan, "greater than", "value", ["number", "priority", "datetime"]),
        new(ConditionOps.GreaterOrEqual, "at least", "value", ["number", "priority", "datetime"]),
        new(ConditionOps.LessThan, "less than", "value", ["number", "priority", "datetime"]),
        new(ConditionOps.LessOrEqual, "at most", "value", ["number", "priority", "datetime"]),
        new(ConditionOps.Between, "between", "range", ["number", "datetime"]),
        new(ConditionOps.Before, "before", "date", ["datetime"]),
        new(ConditionOps.After, "after", "date", ["datetime"]),
        new(ConditionOps.WithinLastMinutes, "within the last (minutes)", "minutes", ["datetime"]),
        new(ConditionOps.OlderThanMinutes, "older than (minutes)", "minutes", ["datetime"]),
        new(ConditionOps.Changed, "changed", "none", ["*"]),
        new(ConditionOps.ChangedTo, "changed to", "value", ["*"]),
        new(ConditionOps.ChangedFrom, "changed from", "value", ["*"])
    ];

    public static readonly IReadOnlyList<string> TemplatePlaceholders =
    [
        "{{ticket.number}}", "{{ticket.subject}}", "{{ticket.priority}}", "{{ticket.status}}", "{{ticket.link}}", "{{ticket.channel}}",
        "{{contact.name}}", "{{contact.firstName}}", "{{contact.email}}", "{{customer.name}}", "{{customer.tier}}", "{{assignee.name}}",
        "{{team.name}}", "{{department.name}}", "{{rule.name}}", "{{now}}", "{{cf:<field key>}}", "{{webhook.<payload field>}}"
    ];

    /// <summary>Standard fields that blueprint transitions can make mandatory (plus "cf:&lt;key&gt;").</summary>
    public static readonly IReadOnlyDictionary<string, string> RequirableFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["severity"] = "Severity", ["categoryId"] = "Category", ["subcategoryId"] = "Subcategory", ["issueTypeId"] = "Issue type",
        ["productId"] = "Product", ["moduleId"] = "Module", ["versionId"] = "Version", ["installationId"] = "Installation",
        ["customerId"] = "Customer", ["contactId"] = "Contact", ["assigneeId"] = "Assignee", ["teamId"] = "Team",
        ["resolutionCode"] = "Resolution code", ["rootCause"] = "Root cause", ["priority"] = "Priority"
    };

    private static ActionParamSpec P(string name, string label, string type, bool required = false, string? description = null, IReadOnlyList<string>? options = null) =>
        new(name, label, type, required, description, options);

    public static readonly IReadOnlyList<ActionSpec> Actions =
    [
        new(ActionTypes.Assign, "Assign / reassign", "Ownership", "Assign to an agent or team, or re-run automatic routing.",
            [P("userId", "Agent", "user"), P("teamId", "Team", "team"), P("route", "Use routing rules", "bool", description: "Pick an agent with the team's routing strategy."),
             P("note", "Note", "string")]),
        new(ActionTypes.TransferDepartment, "Move to department", "Ownership", "Transfer the ticket to another department (team and agent re-evaluated).",
            [P("departmentId", "Department", "department", true), P("teamId", "Team", "team"), P("reason", "Reason", "string")]),
        new(ActionTypes.SetPriority, "Set priority", "Fields", "Change the priority (SLA targets are recalculated).", [P("priority", "Priority", "enum", true, options: Priorities)]),
        new(ActionTypes.SetSeverity, "Set severity", "Fields", "Change the severity.", [P("severity", "Severity", "enum", true, options: Severities)]),
        new(ActionTypes.SetStatus, "Set status", "Fields", "Change the status (respects configured transitions; not allowed on blueprint tickets).",
            [P("statusId", "Status", "status", true), P("substatus", "Substatus", "string"), P("comment", "Comment", "string")]),
        new(ActionTypes.SetSubstatus, "Set substatus", "Fields", "Change the substatus within the current status.", [P("substatus", "Substatus", "string", true)]),
        new(ActionTypes.SetCategory, "Set category", "Fields", "Classify the ticket.", [P("categoryId", "Category", "category", true), P("subcategoryId", "Subcategory", "category")]),
        new(ActionTypes.SetIssueType, "Set issue type", "Fields", "Change the issue type.", [P("issueTypeId", "Issue type", "issueType", true)]),
        new(ActionTypes.SetCustomField, "Set custom field", "Fields", "Set (or clear with null) a ticket custom field. String values support placeholders.",
            [P("key", "Field", "customField", true), P("value", "Value", "any", true)]),
        new(ActionTypes.AddTags, "Add tags", "Fields", "Add tags to the ticket.", [P("tags", "Tags", "tags", true)]),
        new(ActionTypes.RemoveTags, "Remove tags", "Fields", "Remove tags from the ticket.", [P("tags", "Tags", "tags", true)]),
        new(ActionTypes.AddNote, "Add internal note", "Conversation", "Add an internal note (supports placeholders).", [P("bodyHtml", "Note", "template", true)]),
        new(ActionTypes.ReplyWithTemplate, "Reply with template", "Conversation", "Send a reply template to the customer as a public reply.",
            [P("templateId", "Reply template", "replyTemplate", true)]),
        new(ActionTypes.AddWatcher, "Add followers", "Collaboration", "Add agents as watchers/followers.",
            [P("userIds", "Agents", "users", true), P("kind", "Kind", "enum", options: ["Watcher", "Collaborator", "Specialist"])]),
        new(ActionTypes.NotifyAgents, "Notify agents", "Notifications", "In-app notification (plus the agent's e-mail/push preferences).",
            [P("recipients", "Recipients", "list", description: "assignee, supervisors, watchers, team, department", options: NotifyRecipients),
             P("userIds", "Specific agents", "users"), P("roleIds", "Roles", "roles"), P("title", "Title", "template", true), P("message", "Message", "template"),
             P("severity", "Severity", "enum", options: ["Info", "Warning", "Critical"])]),
        new(ActionTypes.SendMessage, "Send e-mail / SMS / WhatsApp", "Notifications", "Send a message to the contact or an agent through a channel.",
            [P("channel", "Channel", "enum", true, options: MessageChannels), P("to", "Recipient", "enum", true, options: ["contact", "assignee", "user", "address"]),
             P("userId", "Agent", "user"), P("address", "Address / number", "string"), P("subject", "Subject", "template"), P("bodyHtml", "Message", "template", true),
             P("templateName", "Channel template (WhatsApp)", "string")]),
        new(ActionTypes.CreateTask, "Create task", "Collaboration", "Create a ticket task.",
            [P("title", "Title", "template", true), P("description", "Description", "template"), P("assigneeId", "Assignee", "user", description: "Agent id or \"assignee\"."),
             P("dueInHours", "Due in (hours)", "int")]),
        new(ActionTypes.CreateChildTicket, "Create child ticket", "Collaboration", "Create a linked child ticket for the same customer.",
            [P("subject", "Subject", "template", true), P("descriptionHtml", "Description", "template"), P("departmentId", "Department", "department"),
             P("priority", "Priority", "enum", options: Priorities), P("tags", "Tags", "tags"), P("route", "Auto-assign", "bool")]),
        new(ActionTypes.InvokeWebhook, "Call webhook / API", "Integrations", "HTTP call to an external system (durable job with retries, HMAC-signed when a secret is set).",
            [P("method", "Method", "enum", true, options: HttpMethods), P("url", "URL", "url", true), P("headers", "Headers", "headers"),
             P("body", "Body (JSON template)", "template"), P("secret", "Signing secret", "secret", description: "Adds X-SupportDesk-Signature: sha256=<HMAC>."),
             P("timeoutSeconds", "Timeout (s)", "int")]),
        new(ActionTypes.UpdateErpRecord, "Update ERP record", "Integrations", "Update the related ERP record through the ERP connector.",
            [P("system", "System", "string", true), P("recordType", "Record type", "string", true), P("recordNumber", "Record number", "template"),
             P("useOriginReference", "Use the ticket's origin record", "bool"), P("changes", "Changes", "json", true)]),
        new(ActionTypes.RequestApproval, "Request approval", "Approvals", "Create an approval request for a user or role.",
            [P("reason", "Reason", "template", true), P("approverUserId", "Approver", "user"), P("approverRoleId", "Approver role", "role"),
             P("type", "Approval type", "string"), P("expiresInDays", "Expires in (days)", "int")]),
        new(ActionTypes.Escalate, "Escalate", "Ownership", "Escalate (supervisors are notified); optionally raise priority and add a specialist.",
            [P("reason", "Reason", "template", true), P("raisePriority", "Raise priority", "bool"), P("toUserId", "Escalate to agent", "user"),
             P("toSupervisor", "Add a department supervisor", "bool")]),
        new(ActionTypes.AiClassify, "AI classify", "AI", "Classify the ticket with AI (optionally applying the suggestion).", [P("apply", "Apply suggestion", "bool")]),
        new(ActionTypes.AiSummarize, "AI summarise", "AI", "Generate an AI summary of the conversation.", [])
    ];

    public static readonly IReadOnlyDictionary<string, ActionSpec> ByType = Actions.ToDictionary(a => a.Type, StringComparer.OrdinalIgnoreCase);

    public static AutomationCatalogDto ToDto() => new(
        Triggers,
        TicketConditionContext.FieldCatalog.Select(f => new ConditionFieldDto(f.Field, f.Label, f.Type)).ToList(),
        Operators,
        Actions.Select(a => new ActionTypeDto(a.Type, a.Label, a.Category, a.Description,
            a.Params.Select(p => new ActionParamDto(p.Name, p.Label, p.Type, p.Required, p.Description, p.Options)).ToList())).ToList(),
        TemplatePlaceholders,
        RequirableFields.Keys.Concat(["cf:<field key>"]).ToList());
}
