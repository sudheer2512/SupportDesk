using SupportDesk.Application.Knowledge;
using SupportDesk.Application.Search;

namespace SupportDesk.UnitTests.Knowledge;

public sealed class KnowledgeRuleTests
{
    [Fact]
    public void Line_diff_marks_inserts_and_deletes()
    {
        var diff = TextDiff.Lines("Intro\nOld step\nOutro", "Intro\nNew step\nExtra\nOutro");
        Assert.Equal(["equal", "delete", "insert", "insert", "equal"], diff.Select(d => d.Op));
        Assert.Equal("Old step", diff[1].Text);
    }

    [Fact]
    public void Markdown_round_trips_common_blocks()
    {
        var html = MarkdownConverter.ToHtml("# Title\n\nSome **bold** and `code`.\n\n- one\n- two\n\n```sql\nSELECT 1;\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |");
        Assert.Contains("<h1>Title</h1>", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<code>code</code>", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("language-sql", html);
        Assert.Contains("<td>2</td>", html);
        var md = MarkdownConverter.FromHtml(html);
        Assert.Contains("# Title", md);
        Assert.Contains("**bold**", md);
        Assert.Contains("- one", md);
        Assert.Contains("```sql", md);
    }

    [Fact]
    public void Markdown_links_cannot_inject_javascript()
    {
        var html = MarkdownConverter.ToHtml("[click](javascript:alert(1))");
        Assert.DoesNotContain("javascript:", html);
    }

    [Fact]
    public void Query_parsing_keeps_a_prefix_only_while_typing()
    {
        var typing = SearchEngine.Parse("password res");
        Assert.Equal("res", typing.Prefix);
        Assert.Null(SearchEngine.Parse("password reset ").Prefix);
        Assert.Contains("password", typing.Terms);
    }

    [Fact]
    public void Highlight_escapes_html_and_marks_matches()
    {
        var parsed = SearchEngine.Parse("rounding ");
        var snippet = SearchEngine.Highlight("<script>x</script> GST Rounding differences appear", parsed);
        Assert.NotNull(snippet);
        Assert.DoesNotContain("<script>", snippet);
        Assert.Contains("<mark>Rounding</mark>", snippet);
    }

    [Fact]
    public void Index_weights_title_above_keywords_above_body()
    {
        var (weights, count) = SearchIndexer.Weigh(new IndexContent("printer", "spooler", "toner", "public", "en", null, null, null));
        Assert.Equal(3f, weights["printer"]);
        Assert.Equal(2f, weights["spooler"]);
        Assert.Equal(1f, weights["toner"]);
        Assert.Equal(3, count);
    }

    [Theory]
    [InlineData("hi", "hi")]
    [InlineData("en_gb", "en-GB")]
    [InlineData("x", null)]
    [InlineData("<script>", null)]
    public void Locales_are_normalised(string input, string? expected) => Assert.Equal(expected, KbRules.NormalizeLocale(input));
}
