using Umcp.Daemon.Generated;

namespace Umcp.Daemon.Mcp;

/// <summary>
/// Lexical catalog search. Local, no embeddings, no API key, no network — G3 is a hard
/// requirement, and a bundled model would cost ~90 MB to do worse than this on 47 short strings.
///
/// Phase 1 is a straightforward token-overlap score with an exact-id fast path. Phase 2 replaces
/// the scorer with BM25 over the skill tree; the interface does not change.
/// </summary>
public static class CatalogSearch
{
    static readonly char[] Split = { ' ', '.', '_', '-', '/', ',', ':', ';', '(', ')', '\t', '\n' };

    public static (ToolEntry entry, double score)[] Search(string query, int limit)
    {
        var terms = Tokens(query);
        if (terms.Length == 0)
            return ToolCatalog.All.Take(limit).Select(e => (e, 0d)).ToArray();

        var scored = new List<(ToolEntry, double)>();
        foreach (var e in ToolCatalog.All)
        {
            var idTokens = Tokens(e.Id);
            var summaryTokens = Tokens(e.Summary);
            double score = 0;

            foreach (var t in terms)
            {
                // An id match is worth far more than a description match: ids are what the caller
                // is actually trying to find.
                if (e.Id.Equals(t, StringComparison.OrdinalIgnoreCase)) score += 10;
                else if (idTokens.Contains(t)) score += 4;
                else if (idTokens.Any(x => x.StartsWith(t, StringComparison.Ordinal))) score += 2;

                if (summaryTokens.Contains(t)) score += 1;
                else if (summaryTokens.Any(x => x.StartsWith(t, StringComparison.Ordinal))) score += 0.5;

                if (e.Skill.Equals(t, StringComparison.OrdinalIgnoreCase)) score += 3;
            }

            if (score > 0) scored.Add((e, score / terms.Length));
        }

        return scored.OrderByDescending(x => x.Item2).ThenBy(x => x.Item1.Id, StringComparer.Ordinal)
                     .Take(limit).ToArray();
    }

    static string[] Tokens(string s) =>
        s.ToLowerInvariant().Split(Split, StringSplitOptions.RemoveEmptyEntries)
         .Select(t => t.Trim('"', '\'', '.'))
         .Where(t => t.Length > 1)
         .ToArray();
}
