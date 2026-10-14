using System.Text.Json;
using SupportDesk.Application.Ai;
using SupportDesk.Application.Ai.Retrieval;
using SupportDesk.Integrations.Ai.Local;

namespace SupportDesk.UnitTests.Ai;

public sealed class EmbeddingTests
{
    [Fact]
    public void Embeddings_are_deterministic_normalised_and_384_dimensional()
    {
        var a = FeatureHashingEmbedder.Embed("Unable to log in after changing my password");
        var b = FeatureHashingEmbedder.Embed("Unable to log in after changing my password");
        Assert.Equal(FeatureHashingEmbedder.Dimensions, a.Length);
        Assert.Equal(a, b);
        var norm = Math.Sqrt(a.Sum(x => (double)x * x));
        Assert.Equal(1.0, norm, 5);
    }

    [Fact]
    public void Similar_texts_are_closer_than_unrelated_texts()
    {
        var query = FeatureHashingEmbedder.Embed("How do I reset my password? I forgot it.");
        var related = FeatureHashingEmbedder.Embed("Password reset: click Forgot password and follow the reset link to choose a new password.");
        var unrelated = FeatureHashingEmbedder.Embed("GST rounding difference on the sales invoice total for the month-end closing.");
        var simRelated = VectorMath.Cosine(query, related);
        var simUnrelated = VectorMath.Cosine(query, unrelated);
        Assert.True(simRelated > simUnrelated + 0.15, $"related {simRelated:0.000} vs unrelated {simUnrelated:0.000}");
        Assert.Equal(1.0, VectorMath.Cosine(query, query), 5);
    }

    [Fact]
    public void Empty_text_gives_a_zero_vector_with_zero_similarity()
    {
        var empty = FeatureHashingEmbedder.Embed("  ");
        Assert.All(empty, v => Assert.Equal(0f, v));
        Assert.Equal(0, VectorMath.Cosine(empty, FeatureHashingEmbedder.Embed("anything")));
    }

    [Fact]
    public void Vectors_round_trip_as_float32_little_endian()
    {
        float[] vector = [1f, -0.5f, 0.25f];
        var bytes = VectorMath.ToBytes(vector);
        Assert.Equal(12, bytes.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, bytes[..4]); // 1.0f little-endian
        Assert.Equal(vector, VectorMath.FromBytes(bytes));
        var embedded = FeatureHashingEmbedder.Embed("round trip");
        Assert.Equal(embedded, VectorMath.FromBytes(VectorMath.ToBytes(embedded)));
    }

    [Fact]
    public void Chunker_splits_long_text_with_overlap_and_keeps_short_text_whole()
    {
        Assert.Single(TextChunker.Chunk("Short article body."));
        var paragraphs = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"Paragraph {i} explains configuration step {i} of the payroll module in some detail."));
        var chunks = TextChunker.Chunk(paragraphs, 300, 80);
        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 400, c));
        Assert.Contains("Paragraph 30", chunks[^1]);
    }
}

public sealed class ClassifierTests
{
    private static readonly (string Text, string Label)[] Corpus =
    [
        ("Payroll run computes wrong PF deduction for employees", "payroll"),
        ("Salary slip shows incorrect provident fund amount", "payroll"),
        ("Payslip PDF has wrong month and PF missing", "payroll"),
        ("Employee salary not credited after payroll posting", "payroll"),
        ("GST total does not match invoice amount rounding", "tax"),
        ("E-invoice upload rejected by GST portal", "tax"),
        ("GSTR-1 JSON has wrong tax rate for invoice lines", "tax"),
        ("Invoice tax calculation mismatch CGST SGST", "tax"),
        ("Cannot log in after password change", "access"),
        ("User locked out, needs password reset", "access"),
        ("Permission denied when opening purchase approvals", "access"),
        ("Forgot password for the warehouse terminal login", "access")
    ];

    private static NaiveBayesClassifier Train()
    {
        var nb = new NaiveBayesClassifier();
        foreach (var (text, label) in Corpus) nb.Train(text, label);
        return nb;
    }

    [Theory]
    [InlineData("PF calculation wrong in this month's payroll for new joiners", "payroll")]
    [InlineData("Rounding difference between GST and the invoice total", "tax")]
    [InlineData("I forgot my password and cannot log in", "access")]
    public void Naive_bayes_predicts_the_right_label_on_a_small_corpus(string text, string expected)
    {
        var prediction = Train().Predict(text);
        Assert.Equal(expected, prediction[0].Label);
        Assert.True(prediction[0].Probability > 0.5, $"p = {prediction[0].Probability:0.000}");
        Assert.Equal(1.0, prediction.Sum(p => p.Probability), 6);
    }

    [Fact]
    public void Local_provider_classifies_with_whitelisted_values_and_confidence()
    {
        var payroll = Guid.NewGuid().ToString();
        var tax = Guid.NewGuid().ToString();
        var access = Guid.NewGuid().ToString();
        var ids = new Dictionary<string, string> { ["payroll"] = payroll, ["tax"] = tax, ["access"] = access };
        var examples = Corpus.Select(c => new ClassificationExample(c.Text, new Dictionary<string, string> { ["category"] = ids[c.Label], ["priority"] = "Medium" })).ToList();
        var options = new Dictionary<string, IReadOnlyList<LabelOption>>
        {
            ["category"] = [new(payroll, "Payroll", ["payroll"]), new(tax, "GST & Tax", ["gst", "tax"]), new(access, "Access & Login › Password reset", ["password", "reset"])],
            ["priority"] = [new("Low", "Low", ["question"]), new("Medium", "Medium", ["issue"]), new("High", "High", ["urgent", "block"]), new("Critical", "Critical", ["down"])]
        };
        var task = new ClassificationTask("Urgent: payroll PF deduction is wrong for 23 employees", options, examples, $"test-{Guid.NewGuid():N}", ["payroll"]);
        var response = new LocalAiProvider().CompleteAsync(new AiChatRequest { Feature = "classification", LocalTask = task }, default).Result;
        using var json = JsonDocument.Parse(response.Text);
        var category = json.RootElement.GetProperty("fields").GetProperty("category");
        Assert.Equal(payroll, category.GetProperty("value").GetString());
        Assert.InRange(category.GetProperty("confidence").GetDouble(), 0.5, 1.0);
        Assert.Equal("High", json.RootElement.GetProperty("fields").GetProperty("priority").GetProperty("value").GetString());
        Assert.Contains("payroll", json.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }
}

public sealed class SentimentTests
{
    [Fact]
    public void Frustration_cues_and_negative_language_are_detected()
    {
        var r = LexiconSentimentAnalyzer.Analyze("This is the third time I'm writing and it is STILL NOT fixed!! Unacceptable. Nobody responds.");
        Assert.True(r.Frustrated);
        Assert.Equal("Frustrated", r.Sentiment);
        Assert.True(r.Score < -0.3);
    }

    [Fact]
    public void Tax_acronyms_are_not_mistaken_for_shouting()
    {
        var r = LexiconSentimentAnalyzer.Analyze("The GST calculation on invoice SI/2026/04512 doesn't match. CGST + SGST shows ₹18,450.36 but the total is off. " +
                                                 "This is blocking month-end closing.");
        Assert.False(r.Frustrated);
        Assert.DoesNotContain("shouting", r.Cues);
    }

    [Fact]
    public void Thanks_is_positive_and_negation_flips_polarity()
    {
        Assert.Equal("Positive", LexiconSentimentAnalyzer.Analyze("Thank you so much, that fixed it. Great support!").Sentiment);
        Assert.True(LexiconSentimentAnalyzer.Analyze("The export is not working").Score < 0);
        Assert.Equal("Neutral", LexiconSentimentAnalyzer.Analyze("Please send the invoice copy for April.").Sentiment);
    }
}
