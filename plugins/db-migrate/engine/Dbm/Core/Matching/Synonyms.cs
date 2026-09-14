using System.Text.Json;

namespace Dbm.Core.Matching;

/// <summary>Abbreviation dictionary + synonym groups (canonical = first group member).</summary>
public sealed class Synonyms
{
    private const string ResourceName = "Dbm.Core.Matching.synonyms.json";
    private static readonly Lazy<string> DefaultJson = new(LoadDefaultJson);

    private readonly Dictionary<string, string[]> _abbreviations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _canonical = new(StringComparer.Ordinal);

    private Synonyms() { }

    /// <summary>The embedded Dbm/Core/Matching/synonyms.json.</summary>
    public static Synonyms Default()
    {
        var synonyms = new Synonyms();
        synonyms.Merge(DefaultJson.Value, ResourceName);
        return synonyms;
    }

    /// <summary>Default merged with each existing file (later files win for abbreviations; groups accumulate). Missing paths are skipped.</summary>
    public static Synonyms Load(params string?[] extraJsonPaths)
    {
        var synonyms = Default();
        foreach (var path in extraJsonPaths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) synonyms.Merge(File.ReadAllText(path), path);
        }
        return synonyms;
    }

    /// <summary>Default + ~/.dbmigrate/synonyms.json (team) + &lt;workspace&gt;/.dbmigrate/synonyms.json (project).</summary>
    public static Synonyms ForProject(Workspace ws) =>
        Load(Path.Combine(UserHome.Dir, "synonyms.json"), Path.Combine(ws.Dir, "synonyms.json"));

    /// <summary>Abbreviation expansion then group canonicalisation; unknown token -> [token]. Output is lower-case.</summary>
    public IReadOnlyList<string> Expand(string token)
    {
        var t = token.ToLowerInvariant();
        IEnumerable<string> words = _abbreviations.TryGetValue(t, out var expansion) ? expansion : new[] { t };
        return words.Select(w => _canonical.TryGetValue(w, out var canonical) ? canonical : w).ToList();
    }

    /// <summary>True when the token is a known abbreviation or group member.</summary>
    public bool Knows(string token)
    {
        var t = token.ToLowerInvariant();
        return _abbreviations.ContainsKey(t) || _canonical.ContainsKey(t);
    }

    private void Merge(string json, string source)
    {
        SynonymsFile file;
        try
        {
            file = JsonSerializer.Deserialize<SynonymsFile>(json, Json.Options) ?? new SynonymsFile(null, null);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid synonyms file '{source}': {ex.Message}", ex);
        }
        foreach (var (key, value) in file.Abbreviations ?? new Dictionary<string, List<string>>())
            _abbreviations[key.ToLowerInvariant()] = value.Select(v => v.ToLowerInvariant()).ToArray();
        foreach (var group in file.Groups ?? new List<List<string>>())
        {
            if (group.Count < 2) continue;
            var members = group.Select(m => m.ToLowerInvariant()).ToList();
            var canonical = members[0];
            // A group that overlaps an existing one absorbs it: every word of the old group moves to the new canonical.
            var affected = new HashSet<string>(members, StringComparer.Ordinal);
            foreach (var member in members)
            {
                if (!_canonical.TryGetValue(member, out var previous)) continue;
                foreach (var (word, c) in _canonical)
                    if (c == previous) affected.Add(word);
            }
            foreach (var word in affected) _canonical[word] = canonical;
        }
    }

    private static string LoadDefaultJson()
    {
        using var stream = typeof(Synonyms).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record SynonymsFile(Dictionary<string, List<string>>? Abbreviations, List<List<string>>? Groups);
}
