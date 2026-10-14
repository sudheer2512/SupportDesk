using SupportDesk.Application.Customers;

namespace SupportDesk.UnitTests.Customers;

public sealed class DuplicateScoringTests
{
    private static readonly Guid CustomerA = Guid.NewGuid();
    private static readonly Guid CustomerB = Guid.NewGuid();

    private static DuplicateProbe P(string name, string? email = null, string? phone = null, Guid? customer = null) =>
        new(Guid.NewGuid(), name, email, phone is null ? [] : [phone], customer);

    [Fact]
    public void Same_email_is_a_high_confidence_duplicate()
    {
        var s = DuplicateScoring.Score(P("Ramesh Kumar", "Ramesh@ABCRetail.demo"), P("R. Kumar", "ramesh@abcretail.demo"));
        Assert.True(s.Score >= 0.85, $"score {s.Score}");
        Assert.Equal("High", s.Confidence);
        Assert.Contains("Same e-mail address", s.Reasons);
    }

    [Fact]
    public void Phone_numbers_match_across_country_code_and_trunk_prefix_variants()
    {
        var s = DuplicateScoring.Score(P("Lata Menon", phone: "+91 98450 12345", customer: CustomerA), P("Lata M", phone: "098450-12345", customer: CustomerA));
        Assert.Contains("Same phone number", s.Reasons);
        Assert.Contains("Same customer", s.Reasons);
        Assert.True(s.Score >= 0.85, $"score {s.Score}");
    }

    [Fact]
    public void Misspelt_name_with_same_phone_in_same_customer_scores_higher_than_phone_alone()
    {
        var withName = DuplicateScoring.Score(P("Ramesh Kumar", phone: "9845012345", customer: CustomerA), P("Ramesh Kumaar", phone: "+919845012345", customer: CustomerA));
        var phoneOnly = DuplicateScoring.Score(P("Ramesh Kumar", phone: "9845012345"), P("Priya Raman", phone: "9845012345"));
        Assert.True(withName.Score > phoneOnly.Score);
        Assert.Contains(withName.Reasons, r => r.StartsWith("Similar name", StringComparison.Ordinal));
    }

    [Fact]
    public void Fuzzy_name_alone_counts_only_within_the_same_customer()
    {
        var same = DuplicateScoring.Score(P("Anita Desai", customer: CustomerA), P("Anita Desaai", customer: CustomerA));
        var other = DuplicateScoring.Score(P("Anita Desai", customer: CustomerA), P("Anita Desaai", customer: CustomerB));
        Assert.True(same.Score >= DuplicateScoring.DefaultThreshold, $"score {same.Score}");
        Assert.Equal(0, other.Score);
    }

    [Fact]
    public void Different_people_do_not_match()
    {
        var s = DuplicateScoring.Score(P("Kavya Reddy", "kavya@greenfield.demo", "9811111111", CustomerA), P("Manoj Shetty", "manoj@greenfield.demo", "9822222222", CustomerA));
        Assert.Equal(0, s.Score);
        Assert.Equal("None", s.Confidence);
    }

    [Theory]
    [InlineData("Dr. Thomas Varghese", "Thomas Varghese", 1.0)]
    [InlineData("Varghese, Thomas", "Thomas Varghese", 1.0)]
    [InlineData("Ramesh K", "Ramesh Kumar", 0.9)]
    [InlineData("José Fernandes", "Jose Fernandes", 1.0)]
    public void Name_similarity_normalises_titles_order_accents_and_initials(string a, string b, double atLeast)
    {
        Assert.True(DuplicateScoring.NameSimilarity(a, b) >= atLeast, $"{a} ~ {b} = {DuplicateScoring.NameSimilarity(a, b)}");
    }

    [Fact]
    public void A_contact_is_not_a_duplicate_of_itself()
    {
        var p = P("Joseph Thomas", "joseph@abcretail.demo");
        Assert.Equal(0, DuplicateScoring.Score(p, p).Score);
    }

    [Fact]
    public void Score_stays_below_one_and_is_monotonic_in_evidence()
    {
        var all = DuplicateScoring.Score(P("Imran Sheikh", "imran@metro.demo", "9800000001", CustomerA), P("Imran Shaikh", "imran@metro.demo", "+91 98000 00001", CustomerA));
        var emailOnly = DuplicateScoring.Score(P("Imran Sheikh", "imran@metro.demo"), P("Someone Else", "imran@metro.demo"));
        Assert.True(all.Score < 1.0);
        Assert.True(all.Score > emailOnly.Score);
    }
}
