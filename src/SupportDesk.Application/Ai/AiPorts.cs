namespace SupportDesk.Application.Ai;

// ───────────────────────────── Provider ports ─────────────────────────────

public sealed record AiChatMessage(string Role, string Content);

/// <summary>
/// One model call. Remote providers use <see cref="System"/>/<see cref="Messages"/>; the built-in local provider computes the
/// answer deterministically from the structured <see cref="LocalTask"/> (and ignores the prompt). Both return the same JSON shape.
/// </summary>
public sealed class AiChatRequest
{
    public required string Feature { get; init; }
    public string System { get; init; } = "";
    public IReadOnlyList<AiChatMessage> Messages { get; init; } = [];
    /// <summary>Ask the provider for a JSON object (OpenAI response_format=json_object; Anthropic: instruction + extraction).</summary>
    public bool JsonMode { get; init; }
    public int MaxTokens { get; init; } = 800;
    public double Temperature { get; init; } = 0.2;
    /// <summary>Structured input for the local provider (one of the *Task records below). Never sent over the network.</summary>
    public object? LocalTask { get; init; }
}

public sealed record AiChatResponse(string Text, int InputTokens, int OutputTokens, string Model, string? FinishReason = null);

/// <summary>Chat/completions model (local heuristics, OpenAI, Azure OpenAI, OpenAI-compatible servers, Anthropic).</summary>
public interface IChatModel
{
    string Provider { get; }
    string Model { get; }
    bool IsLocal { get; }
    Task<AiChatResponse> CompleteAsync(AiChatRequest request, CancellationToken ct);
}

public sealed record AiEmbeddingResponse(IReadOnlyList<float[]> Vectors, int InputTokens, string Model);

/// <summary>Text embedding model (batch). Vectors are L2-normalised by the caller when the provider does not.</summary>
public interface IEmbeddingModel
{
    string Provider { get; }
    string Model { get; }
    bool IsLocal { get; }
    Task<AiEmbeddingResponse> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

public sealed record AiProviderSettings(string Provider, string Model, string? Endpoint, string? ApiKey, TimeSpan Timeout);

/// <summary>Creates provider clients for a tenant's configuration (implemented in SupportDesk.Integrations).</summary>
public interface IAiProviderFactory
{
    IChatModel CreateChat(AiProviderSettings settings);
    IEmbeddingModel CreateEmbedding(AiProviderSettings settings);
}

/// <summary>Raised by provider adapters for transport/protocol failures (mapped to ai_provider_error).</summary>
public sealed class AiProviderException(string message, int? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

public static class AiProviderNames
{
    public const string Local = "Local";
    public const string OpenAI = "OpenAI";
    public const string AzureOpenAI = "AzureOpenAI";
    public const string OpenAICompatible = "OpenAICompatible";
    public const string Anthropic = "Anthropic";
    /// <summary>Statistical features computed in-process (no model call).</summary>
    public const string Builtin = "Builtin";

    public const string LocalChatModel = "local-heuristic-v1";
    public const string LocalEmbeddingModel = "local-hash-384";
    public const int LocalEmbeddingDimensions = 384;

    public static readonly string[] All = [Local, OpenAI, AzureOpenAI, OpenAICompatible, Anthropic];
    public static readonly string[] EmbeddingCapable = [Local, OpenAI, AzureOpenAI, OpenAICompatible];

    public static bool IsLocal(string? provider) => string.IsNullOrEmpty(provider) || provider.Equals(Local, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? provider) =>
        All.FirstOrDefault(p => p.Equals(provider?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "";
}

// ───────────────────────────── Local provider tasks ─────────────────────────────
// Structured inputs for the built-in provider. The Application prepares them from tenant data; the local provider
// (SupportDesk.Integrations/Ai/Local) turns them into the same JSON a remote model is asked to produce.

public sealed record TicketMessage(int Seq, string Kind, string AuthorType, string? AuthorName, bool IsPublic, string Text, DateTime CreatedAt);

/// <summary>Everything the AI features know about a ticket (names resolved; customer text is untrusted).</summary>
public sealed record TicketSnapshot(
    Guid Id,
    long Number,
    string DisplayNumber,
    string Subject,
    string Description,
    string Status,
    string StatusCategory,
    string Priority,
    string Channel,
    string? Department,
    string? Assignee,
    string? Customer,
    string? Contact,
    string? ContactFirstName,
    string? Product,
    string? Version,
    string? Category,
    string? Subcategory,
    string SlaState,
    DateTime? DueAt,
    DateTime? FirstResponseDueAt,
    DateTime CreatedAt,
    DateTime Now,
    bool AwaitingCustomerConfirmation,
    string? ResolutionText,
    string? RootCause,
    IReadOnlyList<TicketMessage> Messages,
    IReadOnlyList<string> OpenTasks,
    IReadOnlyDictionary<string, string> CustomFields,
    bool HasVersionField,
    bool HasAttachments,
    string Language);

public sealed record LabelOption(string Value, string Name, IReadOnlyList<string> Keywords);

public sealed record ClassificationExample(string Text, IReadOnlyDictionary<string, string> Labels);

/// <summary>Fields: category (leaf category id), product, priority, department. Examples train the tenant's naive-Bayes model.</summary>
public sealed record ClassificationTask(string Text, IReadOnlyDictionary<string, IReadOnlyList<LabelOption>> Options,
    IReadOnlyList<ClassificationExample> Examples, string CorpusKey, IReadOnlyList<string> KnownTags);

public sealed record SummaryTask(TicketSnapshot Ticket);

public sealed record SentimentTask(string Text);

/// <summary>A retrieved knowledge source offered to a model (numbered for citations).</summary>
public sealed record RetrievedSource(int Index, string SourceType, Guid SourceId, string Title, string Text, string? Url, double Score, string Visibility);

public sealed record ReplyTask(TicketSnapshot Ticket, IReadOnlyList<RetrievedSource> Sources, string? AgentName, string? Instructions, bool CustomerFacingOnly);

public sealed record AssistantTask(string Question, IReadOnlyList<RetrievedSource> Sources, bool Authenticated, string? Locale);

public sealed record RootCauseEvidence(Guid TicketId, long Number, string Subject, string? RootCauseCode, string? RootCauseLabel, string? Resolution, double Similarity);

public sealed record RootCauseTask(TicketSnapshot Ticket, IReadOnlyList<RootCauseEvidence> Evidence);

public sealed record KnowledgeDraftTask(TicketSnapshot Ticket);

public sealed record TranslationTask(string Text, string TargetLocale, string? SourceLocale);

public sealed record AnalyticsVocabularyEntry(string Field, string Value, string Label);

public sealed record NlAnalyticsTask(string Question, IReadOnlyList<AnalyticsVocabularyEntry> Vocabulary, DateTime Now);

public sealed record InfoCollectorTask(TicketSnapshot Ticket, IReadOnlyList<string> RequiredInfo);

public sealed record ProviderTestTask(string Prompt);

/// <summary>Information items the info-collector agent can ask for.</summary>
public static class InfoItems
{
    public const string Version = "version";
    public const string Environment = "environment";
    public const string Steps = "steps";
    public const string ErrorMessage = "error_message";
    public const string Screenshot = "screenshot";
    public const string ImpactScope = "impact";
    public static readonly string[] All = [Version, Environment, Steps, ErrorMessage, Screenshot, ImpactScope];
    public static readonly string[] Default = [Version, Environment, Steps, ErrorMessage];
}
