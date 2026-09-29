using System.Text;

namespace VspcAutotaskPlugin.Sync;

/// <summary>
/// Company-name matching used by auto-map: exact normalized matches map automatically
/// (CWM parity: "names are same or similar"), close matches become suggestions.
/// </summary>
public static class NameMatcher
{
    private static readonly string[] LegalSuffixes =
    {
        "incorporated", "inc", "llc", "llp", "ltd", "limited", "gmbh", "sarl", "srl",
        "pty", "plc", "corp", "corporation", "company", "co", "sa", "ag", "bv", "nv", "oy", "ab"
    };

    /// <summary>Lowercase, strip punctuation, collapse whitespace, drop trailing legal suffixes.</summary>
    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        var tokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && LegalSuffixes.Contains(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);
        return string.Join(' ', tokens);
    }

    /// <summary>Similarity in [0,1] based on Levenshtein distance over normalized names.</summary>
    public static double Similarity(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return 0;
        if (na == nb) return 1;
        var distance = Levenshtein(na, nb);
        var max = Math.Max(na.Length, nb.Length);
        return 1.0 - (double)distance / max;
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
