namespace Dbm.Core.Matching;

public interface IVectorizer
{
    void Fit(IEnumerable<IReadOnlyList<string>> documents);
    SparseVector Transform(IReadOnlyList<string> tokens);
}

/// <summary>
/// Word + character n-gram TF-IDF. Features: "w:&lt;token&gt;" (weight wordWeight·idf) and "g:&lt;n-gram of '#' + tokens joined by '#' + '#'&gt;"
/// (weight ngramWeight·idf), multiplied by the raw term count; idf = ln((1+N)/(1+df))+1; output L2-normalised.
/// </summary>
public sealed class NgramTfidfVectorizer(int n = 3, double wordWeight = 1.0, double ngramWeight = 0.5) : IVectorizer
{
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private int _documents;

    public void Fit(IEnumerable<IReadOnlyList<string>> documents)
    {
        _documentFrequency.Clear();
        _documents = 0;
        foreach (var document in documents)
        {
            _documents++;
            foreach (var feature in Features(document).Keys)
                _documentFrequency[feature] = _documentFrequency.GetValueOrDefault(feature) + 1;
        }
    }

    public SparseVector Transform(IReadOnlyList<string> tokens)
    {
        var counts = Features(tokens);
        var weights = new Dictionary<string, double>(counts.Count, StringComparer.Ordinal);
        foreach (var (feature, count) in counts)
        {
            var idf = Math.Log((1.0 + _documents) / (1.0 + _documentFrequency.GetValueOrDefault(feature))) + 1.0;
            weights[feature] = count * (feature[0] == 'w' ? wordWeight : ngramWeight) * idf;
        }
        return SparseVector.Normalized(weights);
    }

    private Dictionary<string, int> Features(IReadOnlyList<string> tokens)
    {
        var features = new Dictionary<string, int>(StringComparer.Ordinal);
        if (tokens.Count == 0) return features;
        foreach (var token in tokens) Add("w:" + token);
        var text = "#" + string.Join("#", tokens) + "#";
        for (var i = 0; i + n <= text.Length; i++) Add("g:" + text.Substring(i, n));
        return features;

        void Add(string key) => features[key] = features.GetValueOrDefault(key) + 1;
    }
}
