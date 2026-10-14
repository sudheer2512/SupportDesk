using System.Globalization;
using System.Text;
using SupportDesk.Domain.Rules;

namespace SupportDesk.Application.Customers;

/// <summary>The fields duplicate detection compares (normalised e-mail/phones, display name, customer).</summary>
public sealed record DuplicateProbe(
    Guid Id,
    string FullName,
    string? Email,
    IReadOnlyCollection<string> Phones,
    Guid? CustomerId,
    IReadOnlyCollection<string>? ExtraEmails = null);

public sealed record DuplicateScore(double Score, string Confidence, IReadOnlyList<string> Reasons)
{
    public static readonly DuplicateScore None = new(0, "None", []);
}

/// <summary>
/// Pure contact duplicate scoring. Signals are combined as independent evidence ("noisy-or": 1 − Π(1 − wᵢ)):
/// identical normalised e-mail (0.90), identical phone/WhatsApp number (0.80, compared on the national number so
/// +91 98450 12345 and 098450 12345 match), name similarity (up to 0.60 for the same name, scaled down to 0.30 at
/// 80 % similarity; token order, titles, punctuation and initials are normalised) and, only when there is other
/// evidence, the same customer (0.15). Contacts of two different customers that share nothing but a similar name
/// are not duplicates.
/// </summary>
public static class DuplicateScoring
{
    public const double EmailWeight = 0.90;
    public const double PhoneWeight = 0.80;
    public const double SameNameWeight = 0.60;
    public const double SimilarNameFloor = 0.80;
    public const double SameCustomerWeight = 0.15;
    public const double DefaultThreshold = 0.40;

    private static readonly HashSet<string> Titles = new(StringComparer.Ordinal) { "mr", "mrs", "ms", "miss", "dr", "prof", "sir", "shri", "smt", "jr", "sr" };

    public static DuplicateScore Score(DuplicateProbe a, DuplicateProbe b)
    {
        if (a.Id == b.Id) return DuplicateScore.None;
        var reasons = new List<string>();
        var weights = new List<double>();

        var emailsA = Emails(a);
        var emailsB = Emails(b);
        if (emailsA.Overlaps(emailsB))
        {
            weights.Add(EmailWeight);
            reasons.Add("Same e-mail address");
        }
        else if (emailsA.Select(LocalPart).Where(l => l.Length >= 5).Intersect(emailsB.Select(LocalPart)).Any())
        {
            weights.Add(0.25);
            reasons.Add("Same e-mail user name on a different domain");
        }

        var phonesA = a.Phones.Select(NationalNumber).Where(p => p is not null).ToHashSet();
        var phonesB = b.Phones.Select(NationalNumber).Where(p => p is not null).ToHashSet();
        if (phonesA.Overlaps(phonesB))
        {
            weights.Add(PhoneWeight);
            reasons.Add("Same phone number");
        }

        var similarity = NameSimilarity(a.FullName, b.FullName);
        var hasNameSignal = false;
        if (similarity >= 0.999)
        {
            weights.Add(SameNameWeight);
            reasons.Add("Same name");
            hasNameSignal = true;
        }
        else if (similarity >= SimilarNameFloor)
        {
            // 0.80 → 0.30 … 0.99 → 0.585
            weights.Add(0.30 + (similarity - SimilarNameFloor) * 1.5);
            reasons.Add($"Similar name ({similarity:P0})");
            hasNameSignal = true;
        }

        var sameCustomer = a.CustomerId is not null && a.CustomerId == b.CustomerId;
        if (weights.Count == 0) return DuplicateScore.None;
        if (sameCustomer && (hasNameSignal || weights.Count > 1 || weights[0] >= PhoneWeight))
        {
            weights.Add(SameCustomerWeight);
            reasons.Add("Same customer");
        }
        // A bare similar name across different (known) customers is not evidence of a duplicate.
        if (weights.Count == 1 && hasNameSignal && !sameCustomer) return DuplicateScore.None;

        var score = 1.0 - weights.Aggregate(1.0, (acc, w) => acc * (1.0 - Math.Clamp(w, 0, 1)));
        score = Math.Round(Math.Min(score, 0.999), 3);
        return new DuplicateScore(score, Confidence(score), reasons);
    }

    public static string Confidence(double score) => score >= 0.85 ? "High" : score >= 0.6 ? "Medium" : score > 0 ? "Low" : "None";

    /// <summary>Similarity of two person names in [0,1]: the best of edit-distance similarity on the normalised name, on the
    /// sorted tokens (word order), and an initials-aware token match ("Ramesh K" ~ "Ramesh Kumar").</summary>
    public static double NameSimilarity(string? a, string? b)
    {
        var na = NormalizeName(a);
        var nb = NormalizeName(b);
        if (na.Length == 0 || nb.Length == 0) return 0;
        if (na == nb) return 1;
        var direct = TextNormalization.Similarity(na, nb);
        var ta = na.Split(' ');
        var tb = nb.Split(' ');
        var sorted = TextNormalization.Similarity(string.Join(' ', ta.Order(StringComparer.Ordinal)), string.Join(' ', tb.Order(StringComparer.Ordinal)));
        return Math.Max(Math.Max(direct, sorted), TokenSimilarity(ta, tb));
    }

    /// <summary>Lower case, accents removed, titles and punctuation dropped, whitespace collapsed.</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        var tokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !Titles.Contains(t));
        return string.Join(' ', tokens);
    }

    /// <summary>Digits of the national number (last 10 digits) so country-code variants match; null for short values.</summary>
    public static string? NationalNumber(string? phone)
    {
        var normalized = TextNormalization.NormalizePhone(phone);
        if (normalized is null) return null;
        return normalized.Length > 10 ? normalized[^10..] : normalized;
    }

    private static double TokenSimilarity(string[] a, string[] b)
    {
        // Each token of the shorter name is matched to its best partner; single letters match tokens with that initial.
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shorter.Length == 0) return 0;
        var used = new HashSet<int>();
        double total = 0;
        foreach (var t in shorter)
        {
            var best = 0.0;
            var bestIndex = -1;
            for (var i = 0; i < longer.Length; i++)
            {
                if (used.Contains(i)) continue;
                var s = t.Length == 1 || longer[i].Length == 1
                    ? (t[0] == longer[i][0] ? 0.9 : 0)
                    : TextNormalization.Similarity(t, longer[i]);
                if (s > best) { best = s; bestIndex = i; }
            }
            if (bestIndex >= 0) used.Add(bestIndex);
            total += best;
        }
        // Penalise unmatched extra tokens lightly (middle names are common).
        var coverage = total / shorter.Length;
        var extra = longer.Length - shorter.Length;
        return Math.Max(0, coverage - 0.05 * extra);
    }

    private static HashSet<string> Emails(DuplicateProbe p)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (TextNormalization.NormalizeEmail(p.Email) is { } e) set.Add(e);
        foreach (var x in p.ExtraEmails ?? []) if (TextNormalization.NormalizeEmail(x) is { } n) set.Add(n);
        return set;
    }

    private static string LocalPart(string email) => email[..email.IndexOf('@')];
}
