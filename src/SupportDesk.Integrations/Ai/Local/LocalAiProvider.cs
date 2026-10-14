using SupportDesk.Application.Ai;
using SupportDesk.Application.Common;
using SupportDesk.Domain.Rules;

namespace SupportDesk.Integrations.Ai.Local;

/// <summary>
/// Built-in offline provider. Deterministic, free and private (nothing leaves the process): it turns the structured
/// task of each feature into the same JSON a remote model is asked for — naive-Bayes classification trained on the
/// tenant's own tickets, extractive summaries, lexicon sentiment, grounded template replies with citations, rule-based
/// NL-analytics parsing, information gap detection, root-cause ranking and article drafting. Translation is honestly
/// reported as unsupported.
/// </summary>
public sealed class LocalAiProvider : IChatModel, IEmbeddingModel
{
    public string Provider => AiProviderNames.Local;
    public string Model => AiProviderNames.LocalChatModel;
    public bool IsLocal => true;
    string IEmbeddingModel.Model => AiProviderNames.LocalEmbeddingModel;

    public Task<AiEmbeddingResponse> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var vectors = texts.Select(FeatureHashingEmbedder.Embed).ToList();
        return Task.FromResult(new AiEmbeddingResponse(vectors, texts.Sum(t => t.Length) / 4, AiProviderNames.LocalEmbeddingModel));
    }

    public Task<AiChatResponse> CompleteAsync(AiChatRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        object output = request.LocalTask switch
        {
            ClassificationTask t => Classify(t),
            SummaryTask t => Summarize(t),
            SentimentTask t => Sentiment(t),
            ReplyTask t => Reply(t),
            AssistantTask t => Answer(t),
            RootCauseTask t => new { causes = RootCauseRanker.Rank(t).Select(c => new { cause = c.Label, code = c.Code, likelihood = c.Likelihood, explanation = c.Explanation, evidence = c.Evidence }) },
            KnowledgeDraftTask t => Draft(t),
            TranslationTask => new { supported = false, message = "The built-in provider cannot translate. Configure OpenAI, Azure OpenAI, Anthropic or an OpenAI-compatible model in AI settings." },
            NlAnalyticsTask t => LocalNlAnalyticsParser.Parse(t.Question, t.Vocabulary, t.Now),
            InfoCollectorTask t => Info(t),
            ProviderTestTask => "OK — the built-in local provider is working.",
            _ => throw new NotSupportedException($"The built-in provider has no implementation for '{request.Feature}'.")
        };
        var text = output as string ?? Json.Serialize(output);
        var inputTokens = (request.System.Length + request.Messages.Sum(m => m.Content.Length)) / 4;
        return Task.FromResult(new AiChatResponse(text, inputTokens, Math.Max(1, text.Length / 4), Model, "stop"));
    }

    private static object Classify(ClassificationTask t)
    {
        var textTokens = TextNormalization.Tokenize(t.Text).ToHashSet();
        var fields = new Dictionary<string, object>();
        var explanations = new List<string>();
        foreach (var (field, options) in t.Options)
        {
            var allowed = options.Select(o => o.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var examples = t.Examples.Where(e => e.Labels.TryGetValue(field, out var l) && allowed.Contains(l)).ToList();
            string? value = null;
            double confidence = 0;
            var distinct = examples.Select(e => e.Labels[field]).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (examples.Count >= 5 && distinct == 1 && field != "priority")
            {
                // Every past ticket has the same value (e.g. a single-product tenant): suggest it with support-based confidence.
                value = examples[0].Labels[field];
                confidence = 0.55 + 0.3 * Math.Min(1, examples.Count / 20.0);
            }
            else if (examples.Count >= 5 && distinct >= 2)
            {
                var model = LocalModelCache.GetOrTrain($"{t.CorpusKey}:{field}", () =>
                {
                    var nb = new NaiveBayesClassifier();
                    foreach (var e in examples) nb.Train(e.Text, e.Labels[field]);
                    return nb;
                });
                var prediction = model.Predict(t.Text);
                if (prediction.Count > 0)
                {
                    var (label, p) = prediction[0];
                    var support = model.Support(label);
                    value = label;
                    confidence = p * support / (support + 2.0);
                }
            }
            // Keyword evidence: an option whose name is fully mentioned is strong evidence on its own.
            var keyword = options.Select(o => (Option: o, Overlap: o.Keywords.Count == 0 ? 0 : (double)o.Keywords.Count(k => TextNormalization.Tokenize(k).All(textTokens.Contains)) / o.Keywords.Count))
                .Where(x => x.Overlap > 0).OrderByDescending(x => x.Overlap).ThenByDescending(x => x.Option.Keywords.Count).FirstOrDefault();
            if (keyword.Option is not null)
            {
                var kwConfidence = field == "priority" ? 0.4 + 0.35 * Math.Min(1, keyword.Overlap * 3) : 0.45 + 0.35 * keyword.Overlap;
                if (value is null || (!keyword.Option.Value.Equals(value, StringComparison.OrdinalIgnoreCase) && kwConfidence > confidence))
                {
                    value = keyword.Option.Value;
                    confidence = kwConfidence;
                    explanations.Add($"{field}: keyword match on '{keyword.Option.Name}'");
                }
                else if (keyword.Option.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    confidence = Math.Min(0.97, confidence + 0.15);
                }
            }
            if (value is null && field == "priority")
            {
                value = "Medium";
                confidence = 0.35;
            }
            if (value is not null) fields[field] = new { value, confidence = Math.Round(confidence, 4) };
        }
        var tags = t.KnownTags.Where(tag => TextNormalization.Tokenize(tag).ToList() is { Count: > 0 } tt && tt.All(textTokens.Contains)).Take(3).ToList();
        var labelled = t.Examples.Count;
        var explanation = labelled >= 5
            ? $"Naive-Bayes model trained on {labelled} of your past tickets" + (explanations.Count > 0 ? $"; {string.Join("; ", explanations)}" : "") + "."
            : explanations.Count > 0 ? string.Join("; ", explanations) + "." : "Too little history to learn from; defaults applied.";
        return new { fields, tags, explanation };
    }

    private static object Summarize(SummaryTask t)
    {
        var s = ExtractiveSummarizer.Summarize(t.Ticket);
        return new { summary = s.Text, currentStatus = s.CurrentStatus, pendingActions = s.PendingActions };
    }

    private static object Sentiment(SentimentTask t)
    {
        var r = LexiconSentimentAnalyzer.Analyze(t.Text);
        return new { sentiment = r.Sentiment, score = r.Score, frustrated = r.Frustrated, cues = r.Cues };
    }

    private static object Reply(ReplyTask t)
    {
        var c = GroundedComposer.ComposeReply(t);
        return new { body = c.Body, citations = c.Citations, confidence = c.Confidence, grounded = c.Grounded };
    }

    private static object Answer(AssistantTask t)
    {
        var c = GroundedComposer.ComposeAnswer(t);
        return new { answer = c.Body, citations = c.Citations, confidence = c.Confidence, handoff = !c.Grounded || c.Confidence < 0.45 };
    }

    private static object Draft(KnowledgeDraftTask t)
    {
        var (title, summary, body) = KnowledgeDrafter.Draft(t);
        return new { title, summary, bodyHtml = body };
    }

    private static object Info(InfoCollectorTask t)
    {
        var (missing, request) = InfoCollector.Analyze(t);
        return new { missing, request };
    }
}
