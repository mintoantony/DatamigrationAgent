namespace Dbm.Core.SqlGen;

/// <summary>Order = parents first. Cycles = the cyclic strongly connected components found in the input (members sorted ordinally).
/// CycleEdges = the (child, parent) edges removed to break them.
/// <para>The order of the <c>Cycles</c> and <c>CycleEdges</c> lists themselves is NOT ordinal: it is Tarjan emission order
/// (sink components first), round by round. It is still deterministic, a pure function of the ordinally sorted nodes and edges,
/// so it is identical on every machine and run. Consumers rely on it: SqlGenerator emits PreSql/PostSql in CycleEdges order.</para></summary>
public sealed record TopoResult(List<string> Order, List<List<string>> Cycles, List<(string Child, string Parent)> CycleEdges);

/// <summary>Deterministic dependency ordering for FK graphs (edge = child depends on parent).</summary>
public static class TopoSort
{
    static readonly IComparer<(string Child, string Parent)> EdgeOrder = Comparer<(string Child, string Parent)>.Create((a, b) =>
    {
        var c = string.CompareOrdinal(a.Child, b.Child);
        return c != 0 ? c : string.CompareOrdinal(a.Parent, b.Parent);
    });

    public static TopoResult Sort(IReadOnlyCollection<string> nodes, IReadOnlyCollection<(string Child, string Parent)> edges)
        => Sort(nodes, edges, null);

    /// <param name="preferBreak">Optional predicate (child, parent) → true when the edge is a good one to cut
    /// (e.g. every FK column on the child side is nullable). Self-loops are cut first, then preferred edges, then any edge,
    /// always in ordinal (child, parent) order, one edge per cyclic component per round until no cycle remains.</param>
    public static TopoResult Sort(IReadOnlyCollection<string> nodes, IReadOnlyCollection<(string Child, string Parent)> edges,
        Func<string, string, bool>? preferBreak)
    {
        // Explicit guards: nullable annotations are not enforced at runtime on net8.0.
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        if (nodes.Any(n => n is null)) throw new ArgumentException("Node names must not be null.", nameof(nodes));
        var nodeList = nodes.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var nodeSet = new HashSet<string>(nodeList, StringComparer.Ordinal);
        var live = new SortedSet<(string Child, string Parent)>(EdgeOrder);
        foreach (var e in edges)
            if (nodeSet.Contains(e.Child) && nodeSet.Contains(e.Parent)) live.Add(e);

        var cycles = new List<List<string>>();
        var cycleEdges = new List<(string Child, string Parent)>();
        var firstRound = true;
        while (true)
        {
            var cyclic = StronglyConnected(nodeList, live)
                .Where(c => c.Count > 1 || live.Contains((c[0], c[0])))
                .ToList();
            if (firstRound) { cycles.AddRange(cyclic); firstRound = false; }
            if (cyclic.Count == 0) break;
            foreach (var component in cyclic)
            {
                var members = new HashSet<string>(component, StringComparer.Ordinal);
                var inside = live.Where(e => members.Contains(e.Child) && members.Contains(e.Parent)).ToList();
                var pick = inside.Where(e => e.Child == e.Parent)
                    .Concat(inside.Where(e => preferBreak != null && preferBreak(e.Child, e.Parent)))
                    .Concat(inside)
                    .First();
                live.Remove(pick);
                cycleEdges.Add(pick);
            }
        }

        // Kahn's algorithm by dependency level; ordinal order inside a level.
        var pending = nodeList.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        var children = nodeList.ToDictionary(n => n, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (child, parent) in live) { pending[child]++; children[parent].Add(child); }
        var order = new List<string>(nodeList.Count);
        var level = nodeList.Where(n => pending[n] == 0).ToList();
        while (level.Count > 0)
        {
            order.AddRange(level);
            var next = new List<string>();
            foreach (var n in level)
                foreach (var c in children[n])
                    if (--pending[c] == 0) next.Add(c);
            next.Sort(StringComparer.Ordinal);
            level = next;
        }
        if (order.Count != nodeList.Count)
            throw new InvalidOperationException("Topological sort failed to break every cycle.");
        return new TopoResult(order, cycles, cycleEdges);
    }

    /// <summary>Tarjan's algorithm, iterative (no recursion depth limit). Components are returned with members sorted ordinally.</summary>
    static List<List<string>> StronglyConnected(List<string> nodes, SortedSet<(string Child, string Parent)> edges)
    {
        var adjacency = nodes.ToDictionary(n => n, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (child, parent) in edges) adjacency[child].Add(parent);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var result = new List<List<string>>();
        var counter = 0;
        foreach (var root in nodes)
        {
            if (index.ContainsKey(root)) continue;
            var work = new Stack<(string Node, int NextChild)>();
            Visit(root);
            work.Push((root, 0));
            while (work.Count > 0)
            {
                var (v, i) = work.Pop();
                var next = adjacency[v];
                if (i < next.Count)
                {
                    work.Push((v, i + 1));
                    var w = next[i];
                    if (!index.ContainsKey(w)) { Visit(w); work.Push((w, 0)); }
                    else if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
                    continue;
                }
                if (low[v] == index[v])
                {
                    var component = new List<string>();
                    string x;
                    do { x = stack.Pop(); onStack.Remove(x); component.Add(x); } while (x != v);
                    component.Sort(StringComparer.Ordinal);
                    result.Add(component);
                }
                if (work.Count > 0)
                {
                    var caller = work.Peek().Node;
                    low[caller] = Math.Min(low[caller], low[v]);
                }
            }
        }
        return result;

        void Visit(string n)
        {
            index[n] = low[n] = counter++;
            stack.Push(n);
            onStack.Add(n);
        }
    }
}
