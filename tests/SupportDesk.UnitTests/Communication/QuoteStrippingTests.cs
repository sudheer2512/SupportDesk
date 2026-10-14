using SupportDesk.Application.Communication;
using SupportDesk.Domain.Rules;

namespace SupportDesk.UnitTests.Communication;

public sealed class QuoteStrippingTests
{
    [Fact]
    public void Gmail_quote_is_removed_and_kept_separately()
    {
        var (reply, quoted) = QuoteStripper.SplitHtml(
            "<div dir=\"ltr\">The repair fixed most SKUs.</div><br><div class=\"gmail_quote\"><div class=\"gmail_attr\">On Mon, 12 Oct 2026, Meera wrote:<br></div>" +
            "<blockquote class=\"gmail_quote\">Hi Anita, please run the repair.</blockquote></div>");
        Assert.Contains("The repair fixed most SKUs.", reply);
        Assert.DoesNotContain("please run the repair", reply);
        Assert.Contains("please run the repair", quoted);
    }

    [Fact]
    public void Outlook_reply_separator_is_removed()
    {
        var (reply, quoted) = QuoteStripper.SplitHtml(
            "<div>Still failing for 3 vendors.</div><hr style=\"display:inline-block\"><div id=\"divRplyFwdMsg\" dir=\"ltr\"><b>From:</b> Arjun<br><b>Sent:</b> Monday</div><div>Please re-post the voucher.</div>");
        Assert.Equal("Still failing for 3 vendors.", TextNormalization.HtmlToText(reply).Trim());
        Assert.NotNull(quoted);
    }

    [Fact]
    public void Apple_mail_cite_blockquote_and_on_wrote_line_are_removed()
    {
        var (reply, quoted) = QuoteStripper.SplitHtml(
            "<div>Thanks, printing again.</div><div><br><div>On 12 Oct 2026, at 10:15, Wings Support &lt;support@wings.demo&gt; wrote:</div><blockquote type=\"cite\"><div>Re-select the printer.</div></blockquote></div>");
        var text = TextNormalization.HtmlToText(reply);
        Assert.Contains("Thanks, printing again.", text);
        Assert.DoesNotContain("Re-select the printer", text);
        Assert.Contains("Re-select the printer", quoted);
    }

    [Fact]
    public void Generic_on_wrote_blockquote_is_removed()
    {
        var (reply, quoted) = QuoteStripper.SplitHtml("<p>Got it.</p><p>On Tue, Oct 13, 2026 Arjun Nair wrote:</p><blockquote><p>Old text</p></blockquote>");
        Assert.Equal("Got it.", TextNormalization.HtmlToText(reply).Trim());
        Assert.Contains("Old text", quoted);
    }

    [Fact]
    public void Messages_without_quotes_are_unchanged_and_quote_only_messages_are_kept()
    {
        const string plain = "<p>New issue: the GST report is blank.</p><blockquote>A customer quote we were sent</blockquote>";
        Assert.Equal((plain, (string?)null), QuoteStripper.SplitHtml(plain));
        // Nothing but quoted history → keep everything rather than produce an empty message.
        const string onlyQuote = "<div class=\"gmail_quote\">On Mon, x wrote:<blockquote>old</blockquote></div>";
        Assert.Equal((onlyQuote, (string?)null), QuoteStripper.SplitHtml(onlyQuote));
    }

    [Fact]
    public void Plain_text_replies_are_split_at_quote_markers()
    {
        var (reply, quoted) = EmailThreading.SplitQuotedText("Works now, thanks!\n\nOn Mon, 12 Oct 2026 at 10:00, Wings Support wrote:\n> Please restart the service.\n> Regards");
        Assert.Equal("Works now, thanks!", reply);
        Assert.StartsWith("On Mon", quoted);

        var (outlookReply, _) = EmailThreading.SplitQuotedText("See attached.\n-----Original Message-----\nFrom: support\nSent: Monday");
        Assert.Equal("See attached.", outlookReply);
    }
}
