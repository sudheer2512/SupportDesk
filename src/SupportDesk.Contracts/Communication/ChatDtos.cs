namespace SupportDesk.Contracts.Communication;

/// <summary>Public chat widget configuration (anonymous).</summary>
public sealed record ChatWidgetConfigDto(string WidgetKey, string Title, string? BrandSlug, string? BrandName, string PrimaryColor, string AccentColor,
    string Greeting, bool BotEnabled, bool RequireEmail, bool IdentityVerificationEnabled, bool AgentsOnline, string? OfflineMessage);

public sealed record StartChatRequest
{
    public string? Name { get; init; }
    public string? Email { get; init; }
    /// <summary>First message (optional).</summary>
    public string? Message { get; init; }
    public string? PageUrl { get; init; }
    public string? Locale { get; init; }
}

/// <summary>Returned once when a chat starts. The visitor token is shown only here (only its hash is stored).</summary>
public sealed record ChatSessionDto(Guid ConversationId, string VisitorToken, string Status, IReadOnlyList<ChatMessageDto> Messages);

public sealed record ChatMessageDto(Guid Id, string Author, string? AuthorName, string Text, DateTime At, IReadOnlyList<ChatCitationDto>? Citations = null);

public sealed record ChatCitationDto(int Index, string Title, string? Url);

public sealed record ChatStateDto(Guid ConversationId, string Status, string? AgentName, bool IdentityVerified, Guid? TicketId, IReadOnlyList<ChatMessageDto> Messages);

public sealed record VisitorMessageRequest(string Text);

public sealed record ChatVerifyRequest(string Email);

public sealed record ChatVerifyConfirmRequest(string Code);

public sealed record ChatVerifyResponse(string Status, string? DemoCode, string? Message);

public sealed record ChatRatingRequest(int Rating, string? Comment);
