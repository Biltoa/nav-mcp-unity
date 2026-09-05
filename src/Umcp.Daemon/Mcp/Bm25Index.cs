namespace Umcp.Daemon.Mcp;

public sealed record SearchDoc(string Id, string Kind, string Title, string Body, double Boost = 1.0);

public sealed record SearchHit(SearchDoc Doc, double Score);

/// <summary>
/// BM25 over the tool catalog and the skill tree. Local, no embeddings, no API key, no network —
/// G3 is a hard requirement, and a bundled embedding model would cost ~90 MB to do no better on a
/// few hundred short strings. Anthropic ships regex- and BM25-based tool search for the same
/// reason.
///
/// The scorer is the textbook one (k1 = 1.2, b = 0.75) with two additions that matter here:
/// a per-document boost, so a skill node outranks the tools inside it for a broad query, and an
/// exact-id fast path, because a caller who types a tool id is not searching, they are addressing.
/// </summary>
public sealed class Bm25Index
{
    const double K1 = 1.2;
    const double B = 0.75;

    readonly List<SearchDoc> _docs = new();
    readonly List<Dictionary<string, int>> _termFreq = new();
    readonly List<int> _lengths = new();
    readonly Dictionary<string, int> _docFreq = new(StringComparer.Ordinal);
    double _avgLength;

    static readonly char[] Split =
        { ' ', '.', '_', '-', '/', ',', ':', ';', '(', ')', '[', ']', '{', '}', '"', '\'', '\t', '\n', '\r', '?', '!', '<', '>', '|', '=', '*', '#', '`' };

    static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    { "the", "and", "for", "with", "that", "this", "from", "into", "are", "was", "its", "not", "you", "your", "use", "using" };

    public int Count => _docs.Count;

    public void Add(SearchDoc doc)
    {
        var terms = Tokenise(doc.Title + " " + doc.Id + " " + doc.Body);
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in terms) tf[t] = tf.GetValueOrDefault(t) + 1;

        _docs.Add(doc);
        _termFreq.Add(tf);
        _lengths.Add(terms.Count);
        foreach (var t in tf.Keys) _docFreq[t] = _docFreq.GetValueOrDefault(t) + 1;
    }

    public void Build() => _avgLength = _lengths.Count == 0 ? 1 : _lengths.Average();

    public IReadOnlyList<SearchHit> Search(string query, int limit, string? kind = null)
    {
        var terms = Tokenise(query);
        var results = new List<SearchHit>();
        var n = _docs.Count;

        for (var i = 0; i < n; i++)
        {
            var doc = _docs[i];
            if (kind is not null && doc.Kind != kind) continue;

            // Addressing, not searching.
            if (string.Equals(doc.Id, query.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new SearchHit(doc, 1000));
                continue;
            }

            double score = 0;
            var len = _lengths[i];
            foreach (var term in terms)
            {
                if (!_termFreq[i].TryGetValue(term, out var f)) continue;
                var df = _docFreq[term];
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                score += idf * (f * (K1 + 1)) / (f + K1 * (1 - B + B * len / _avgLength));
            }

            // A term appearing in the id itself is a much stronger signal than one buried in prose.
            foreach (var term in terms)
                if (doc.Id.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 1.5;

            if (score > 0) results.Add(new SearchHit(doc, score * doc.Boost));
        }

        return results
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Doc.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    static List<string> Tokenise(string text)
    {
        var result = new List<string>();
        foreach (var raw in text.ToLowerInvariant().Split(Split, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (t.Length < 2 || Stop.Contains(t)) continue;
            result.Add(t);
            // Cheap plural folding: "materials" also matches "material".
            if (t.Length > 3 && t.EndsWith('s') && !t.EndsWith("ss")) result.Add(t[..^1]);
        }
        return result;
    }
}
