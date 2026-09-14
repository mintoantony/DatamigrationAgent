using System.Text.Json.Serialization;

namespace Dbm.Core.Matching;

/// <summary>Sparse feature vector (feature name -> weight). Vectors produced by the vectorizer are L2-normalised, so Dot = cosine.</summary>
public sealed class SparseVector
{
    public Dictionary<string, double> W { get; init; } = new();

    [JsonIgnore] public bool IsEmpty => W.Count == 0;

    public double Dot(SparseVector other)
    {
        var (small, large) = W.Count <= other.W.Count ? (W, other.W) : (other.W, W);
        double sum = 0;
        foreach (var (key, value) in small)
        {
            if (large.TryGetValue(key, out var o)) sum += value * o;
        }
        return sum;
    }

    /// <summary>L2-normalised copy; zero weights are dropped; an all-zero input gives an empty vector.</summary>
    public static SparseVector Normalized(Dictionary<string, double> weights)
    {
        var norm = Math.Sqrt(weights.Values.Sum(v => v * v));
        if (norm == 0) return new SparseVector();
        var w = new Dictionary<string, double>(weights.Count, StringComparer.Ordinal);
        foreach (var (key, value) in weights)
        {
            if (value != 0) w[key] = value / norm;
        }
        return new SparseVector { W = w };
    }
}
