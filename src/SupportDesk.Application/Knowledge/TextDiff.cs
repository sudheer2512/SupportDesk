using SupportDesk.Contracts.Knowledge;

namespace SupportDesk.Application.Knowledge;

/// <summary>
/// Line diff (longest common subsequence) used to compare article versions. Inputs are the plain-text renderings of
/// the bodies, one paragraph/list item per line. Large inputs fall back to a prefix/suffix trim with a block replace so
/// the cost stays bounded.
/// </summary>
public static class TextDiff
{
    private const int MaxCells = 4_000_000;

    public static IReadOnlyList<DiffLineDto> Lines(string? from, string? to)
    {
        var a = Split(from);
        var b = Split(to);
        // Trim the common prefix and suffix first (most edits are local).
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        var endA = a.Length - 1;
        var endB = b.Length - 1;
        while (endA >= start && endB >= start && a[endA] == b[endB]) { endA--; endB--; }

        var result = new List<DiffLineDto>();
        for (var i = 0; i < start; i++) result.Add(new DiffLineDto("equal", a[i]));
        var midA = a[start..(endA + 1)];
        var midB = b[start..(endB + 1)];
        if ((long)(midA.Length + 1) * (midB.Length + 1) > MaxCells)
        {
            result.AddRange(midA.Select(l => new DiffLineDto("delete", l)));
            result.AddRange(midB.Select(l => new DiffLineDto("insert", l)));
        }
        else
        {
            result.AddRange(Lcs(midA, midB));
        }
        for (var i = endA + 1; i < a.Length; i++) result.Add(new DiffLineDto("equal", a[i]));
        return result;
    }

    private static IEnumerable<DiffLineDto> Lcs(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var len = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        for (var j = m - 1; j >= 0; j--)
            len[i, j] = a[i] == b[j] ? len[i + 1, j + 1] + 1 : Math.Max(len[i + 1, j], len[i, j + 1]);
        int x = 0, y = 0;
        var output = new List<DiffLineDto>(n + m);
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { output.Add(new DiffLineDto("equal", a[x])); x++; y++; }
            else if (len[x + 1, y] >= len[x, y + 1]) output.Add(new DiffLineDto("delete", a[x++]));
            else output.Add(new DiffLineDto("insert", b[y++]));
        }
        while (x < n) output.Add(new DiffLineDto("delete", a[x++]));
        while (y < m) output.Add(new DiffLineDto("insert", b[y++]));
        return output;
    }

    private static string[] Split(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
}
