namespace SupportDesk.Application.Notifications;

/// <summary>
/// Deep links into the agent desk (relative to the desk origin) for notifications, e-mail digests, push payloads and
/// search hits. They must match the desk routes (clients/projects/desk/src/app/app.routes.ts and each feature's
/// *.routes.ts); DeskLinkTests checks every link literal in the application layer against the known routes.
/// </summary>
public static class DeskLinks
{
    public static string Ticket(Guid ticketId) => $"/tickets/{ticketId}";

    /// <summary>A ticket list in the workspace (<c>?queue=mine|unassigned|open|…|deleted</c>).</summary>
    public static string TicketQueue(string queueKey) => $"/tickets?queue={Uri.EscapeDataString(queueKey)}";

    /// <summary>A live chat in the chat console (the selected chat is <c>?c=</c>).</summary>
    public static string Chat(Guid conversationId) => $"/inbox/chat?c={conversationId}";

    /// <summary>Any conversation in the omnichannel inbox (the selected conversation is <c>?c=</c>).</summary>
    public static string Conversation(Guid conversationId) => $"/inbox?c={conversationId}";

    public static string Customer(Guid customerId) => $"/customers/{customerId}";
    public static string Contact(Guid contactId) => $"/customers/contacts/{contactId}";
    public static string Contract(Guid contractId) => $"/products/contracts/{contractId}";
    public static string KbArticle(Guid articleId) => $"/kb/articles/{articleId}";
    public static string CommunityTopic(Guid topicId) => $"/kb/community/{topicId}";

    public const string Approvals = "/approvals";
    public const string Notifications = "/notifications";
    public const string AiAnomalies = "/ai/anomalies";
    public const string AiProposals = "/ai/proposals";
    public const string AdminAi = "/admin/ai";
    public const string AdminWebhooks = "/admin/webhooks";
}
