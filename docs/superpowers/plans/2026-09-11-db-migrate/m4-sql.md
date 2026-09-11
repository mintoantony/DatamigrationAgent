# Milestone 4 — SQL Generation

> Part of the db-migrate plan. Read 00-overview.md (Global Constraints + Shared contracts) before any task; every task implicitly includes the Global Constraints.

**Goal:** turn the approved mapping (C12) into a reviewable, validated migration plan (C13): one task per mapped target table in FK-dependency order, with generated source queries, key aliases, identity/LOB handling and FK-cycle pre/post statements (T4.1–T4.2). The plan is validated live against both databases without changing them (T4.3), refined by the `sql-engineer` subagent through the standard review loop and exported as a DBA-readable script pack (T4.4), and reviewed in the SQL screen with highlighted, line-commentable code, direct editing and per-task version diffs (T4.5).

**Verified facts this milestone relies on (SQL Server 2025 LocalDB, 2026-09-11):**

- `EXEC sp_describe_first_result_set @tsql = @q` returns one row per result column with `is_hidden`, `name`, `is_nullable`, `system_type_name` (`int`, `varchar(100)`, `nvarchar(max)`, `decimal(19,4)`, `datetime2(0)`, `nvarchar(50)` — lengths in **characters**), `precision`, `scale` (same meaning as `sys.columns`: `int` 10/0, `datetime` 23/3, `money` 19/4). An unknown column raises error 207 `Invalid column name 'X'.` followed by 11501 `The batch could not be analyzed because of compile errors.`; an unknown table raises 208 + 11529.
- **`SET PARSEONLY ON; <stmt>; SET PARSEONLY OFF;` in ONE batch EXECUTES `<stmt>`** (both SETs act at parse time, so PARSEONLY is off again when the batch runs — an `ALTER TABLE … NOCHECK CONSTRAINT` really disabled the FK and a `CREATE TABLE #stg` really created the table). The safe form is **three separate batches on the same connection**: `SET PARSEONLY ON;` → `<stmt>` → `SET PARSEONLY OFF;`. Then nothing executes, syntax errors are reported (`Incorrect syntax near 'NOCHEK'.`, `A MERGE statement must be terminated by a semi-colon (;).`), unknown objects/columns are **not** reported (parse only), and the session is back to normal after the OFF batch even when the middle batch failed. `SqlValidator` uses the three-batch form on a non-pooled connection.
- `SELECT COUNT_BIG(*) FROM (\n<query>\n) AS q` works even when `<query>` ends with a `--` comment line.

**Upstream code this milestone builds on (all verified by executing M0–M3 and then this plan in the integration repo):** `SampleCatalogs` / `SampleMappings` (T3.2; ground-truth expressions `s.[COL]`, `app.Orders` kind `merge` with the C15 `From`, `TriggerCount = 1` on `app.Orders`), `SampleDatabases.CreateAsync(scale, seed)` → `SamplePair` (`IAsyncDisposable`), `SampleProject.CreateAsync(pair)` and `TempProject.Create()` (T2.1), `TestWorkspace.OpenServices()`, `CliRunner.RunAsync(ws, null, args…)` and `WebTestServer.StartAsync(ws, factory)` (M1 test support), `ExportEndpoints.Register` / `WebExport.BuildHtml` (T2.8), `TypeCompat` (T3.1), `CliContext.OpenProject()` / `CliFailure` (T1.8). Each task's **Interfaces** lists exactly what it uses.

---

### Task 4.1: SqlQuote, SQL plan payload, topological sort

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/Sql/SqlQuote.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlPlanPayload.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/SqlGen/TopoSort.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/TopoSortTests.cs` (also holds `SqlQuoteTests`)

**Interfaces:**
- Consumes: nothing beyond the BCL.
- Produces (C3, C13 — exact):
  - `public static class SqlQuote { string Ident(string name); string Table(string schema, string name); string TableKey(string key); string Literal(string value); }` (`TableKey` throws `ArgumentException` when the key has no `schema.` part).
  - `SqlPlanPayload`, `TaskPlan`, `ColumnBinding`, `ValidationReport` exactly as C13, **plus** these additions: `SqlPlanPayload.Errors` (`List<string>`, global errors of the last validation), `SqlPlanPayload.ErrorCount()` / `WarningCount()` (methods, not serialised), `TaskPlan.MappingHash` (`string?`, lower-hex SHA-256 of the TableMap JSON the task was generated from; drives carry-over in T4.2).
  - `public sealed record TopoResult(List<string> Order, List<List<string>> Cycles, List<(string Child, string Parent)> CycleEdges);`
  - `public static class TopoSort { TopoResult Sort(IReadOnlyCollection<string> nodes, IReadOnlyCollection<(string Child, string Parent)> edges); TopoResult Sort(IReadOnlyCollection<string> nodes, IReadOnlyCollection<(string Child, string Parent)> edges, Func<string, string, bool>? preferBreak); }` — edge = child depends on parent; `Order` = parents first, by dependency level (Kahn), ordinal order inside a level; cycles found with Tarjan's SCC (self-loops included) and broken one edge per cyclic component per round: self-loop first, then edges where `preferBreak(child, parent)` is true, then any edge, always in ordinal `(child, parent)` order.

- [ ] **Step 1: Write the failing tests**

`plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/TopoSortTests.cs`:

```csharp
using Dbm.Core.Sql;
using Dbm.Core.SqlGen;
using Xunit;

namespace Dbm.Tests.Unit.SqlGen;

public class TopoSortTests
{
    static readonly string[] SampleNodes = ["app.OrderLines", "app.Orders", "app.Customers", "app.Addresses", "app.Products", "app.AuditEvents"];
    static readonly (string Child, string Parent)[] SampleEdges =
    [
        ("app.Addresses", "app.Customers"),   // FK_Addresses_Customers (CustomerId NOT NULL)
        ("app.Customers", "app.Addresses"),   // FK_Customers_PrimaryAddress (PrimaryAddressId NULL) -> cycle
        ("app.Orders", "app.Customers"),
        ("app.Orders", "app.Addresses"),
        ("app.OrderLines", "app.Orders"),
        ("app.OrderLines", "app.Products"),
    ];

    [Fact]
    public void Chain_puts_parents_first()
    {
        var r = TopoSort.Sort(["c", "b", "a"], [("c", "b"), ("b", "a")]);
        Assert.Equal(["a", "b", "c"], r.Order);
        Assert.Empty(r.Cycles);
        Assert.Empty(r.CycleEdges);
    }

    [Fact]
    public void Independent_nodes_are_ordinal_sorted()
    {
        var r = TopoSort.Sort(["app.b", "app.C", "app.a"], []);
        Assert.Equal(["app.C", "app.a", "app.b"], r.Order);   // ordinal: 'C' (0x43) < 'a' (0x61)
    }

    [Fact]
    public void Sample_cycle_is_broken_on_the_nullable_edge()
    {
        var nullable = new HashSet<(string, string)> { ("app.Customers", "app.Addresses"), ("app.Orders", "app.Addresses") };
        var r = TopoSort.Sort(SampleNodes, SampleEdges, (c, p) => nullable.Contains((c, p)));
        Assert.Equal(["app.AuditEvents", "app.Customers", "app.Products", "app.Addresses", "app.Orders", "app.OrderLines"], r.Order);
        var cycle = Assert.Single(r.Cycles);
        Assert.Equal(["app.Addresses", "app.Customers"], cycle);
        Assert.Equal([("app.Customers", "app.Addresses")], r.CycleEdges);
    }

    [Fact]
    public void Without_preference_the_first_edge_in_ordinal_order_is_cut()
    {
        var r = TopoSort.Sort(SampleNodes, SampleEdges);
        Assert.Equal([("app.Addresses", "app.Customers")], r.CycleEdges);
        Assert.Equal(["app.Addresses", "app.AuditEvents", "app.Products", "app.Customers", "app.Orders", "app.OrderLines"], r.Order);
    }

    [Fact]
    public void Self_reference_is_a_cycle_edge()
    {
        var r = TopoSort.Sort(["hr.Employee", "hr.Dept"], [("hr.Employee", "hr.Employee"), ("hr.Employee", "hr.Dept")]);
        Assert.Equal(["hr.Dept", "hr.Employee"], r.Order);
        Assert.Equal(["hr.Employee"], Assert.Single(r.Cycles));
        Assert.Equal([("hr.Employee", "hr.Employee")], r.CycleEdges);
    }

    [Fact]
    public void Three_node_cycle_needs_one_cut()
    {
        var r = TopoSort.Sort(["a", "b", "c"], [("a", "b"), ("b", "c"), ("c", "a")]);
        Assert.Equal([("a", "b")], r.CycleEdges);
        Assert.Equal(["a", "c", "b"], r.Order);
        Assert.Equal(["a", "b", "c"], Assert.Single(r.Cycles));
    }

    [Fact]
    public void Two_separate_cycles_are_both_broken()
    {
        var r = TopoSort.Sort(["a", "b", "x", "y"], [("a", "b"), ("b", "a"), ("x", "y"), ("y", "x")]);
        Assert.Equal(2, r.Cycles.Count);
        Assert.Equal([("a", "b"), ("x", "y")], r.CycleEdges);
        Assert.Equal(["a", "x", "b", "y"], r.Order);
    }

    [Fact]
    public void Unknown_and_duplicate_edges_are_ignored()
    {
        var r = TopoSort.Sort(["a", "b"], [("b", "a"), ("b", "a"), ("b", "zzz"), ("qqq", "a")]);
        Assert.Equal(["a", "b"], r.Order);
        Assert.Empty(r.CycleEdges);
    }

    [Fact]
    public void Result_is_deterministic_regardless_of_input_order()
    {
        var a = TopoSort.Sort(SampleNodes, SampleEdges);
        var b = TopoSort.Sort(Enumerable.Reverse(SampleNodes).ToArray(), Enumerable.Reverse(SampleEdges).ToArray());
        Assert.Equal(a.Order, b.Order);
        Assert.Equal(a.CycleEdges, b.CycleEdges);
    }
}

public class SqlQuoteTests
{
    [Fact] public void Ident_doubles_closing_brackets() => Assert.Equal("[Order]]Lines]", SqlQuote.Ident("Order]Lines"));
    [Fact] public void Table_quotes_both_parts() => Assert.Equal("[app].[Orders]", SqlQuote.Table("app", "Orders"));
    [Fact] public void TableKey_splits_on_first_dot() => Assert.Equal("[dbo].[My.Table]", SqlQuote.TableKey("dbo.My.Table"));
    [Fact] public void TableKey_rejects_keys_without_schema() => Assert.Throws<ArgumentException>(() => SqlQuote.TableKey("Customer"));
    [Fact] public void Literal_is_unicode_with_doubled_quotes() => Assert.Equal("N'O''Brien'", SqlQuote.Literal("O'Brien"));
}
```

Note: use `Enumerable.Reverse(array)`, not `array.Reverse()` — with `LangVersion latest` on the .NET 10 SDK the latter binds to the in-place `MemoryExtensions.Reverse(Span<T>)` and returns `void`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.SqlGen.TopoSortTests|FullyQualifiedName~Dbm.Tests.Unit.SqlGen.SqlQuoteTests"`
Expected: build FAILS with `error CS0234: The type or namespace name 'SqlGen' does not exist in the namespace 'Dbm.Core'` (and `CS0103: The name 'SqlQuote' does not exist`).

- [ ] **Step 3: Implement**

`plugins/db-migrate/engine/Dbm/Core/Sql/SqlQuote.cs`:

```csharp
namespace Dbm.Core.Sql;

/// <summary>T-SQL quoting helpers. Every identifier or literal that the engine splices into SQL goes through here.</summary>
public static class SqlQuote
{
    /// <summary>Bracket-quotes an identifier: <c>Order]Lines</c> becomes <c>[Order]]Lines]</c>.</summary>
    public static string Ident(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "[" + name.Replace("]", "]]") + "]";
    }

    /// <summary><c>[schema].[name]</c>.</summary>
    public static string Table(string schema, string name) => Ident(schema) + "." + Ident(name);

    /// <summary>"dbo.Customer" becomes "[dbo].[Customer]" (split on the first '.').</summary>
    public static string TableKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var dot = key.IndexOf('.');
        if (dot <= 0 || dot == key.Length - 1)
            throw new ArgumentException($"Expected a 'schema.table' key but got '{key}'.", nameof(key));
        return Table(key[..dot], key[(dot + 1)..]);
    }

    /// <summary>Unicode string literal with embedded quotes doubled: <c>O'Brien</c> becomes <c>N'O''Brien'</c>.</summary>
    public static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "N'" + value.Replace("'", "''") + "'";
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlPlanPayload.cs`:

```csharp
namespace Dbm.Core.SqlGen;

/// <summary>The SQL phase artifact (contract C13). Serialised with <c>Dbm.Core.Json.Options</c> (camelCase).</summary>
public sealed class SqlPlanPayload
{
    public List<string> PreSql { get; set; } = new();
    public List<string> PostSql { get; set; } = new();
    public List<string> Order { get; set; } = new();                        // task ids in execution order
    public Dictionary<string, TaskPlan> Tasks { get; set; } = new();        // key = task id "T01", "T02", ... (numbered in Order)
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();                       // M4 addition: global errors from the last validation

    public int ErrorCount() => Errors.Count + Tasks.Values.Sum(t => t.Errors.Count);
    public int WarningCount() => Warnings.Count + Tasks.Values.Sum(t => t.Warnings.Count);
}

public sealed class TaskPlan
{
    public string Target { get; set; } = "";              // "app.Orders"
    public string Mode { get; set; } = "direct";          // "direct" | "staging_merge"
    public string SourceQuery { get; set; } = "";         // "SELECT <expr> AS [TargetCol], ..., s.[k] AS [__k0] FROM ... [WHERE ...]" (no ORDER BY)
    public List<string> KeyColumns { get; set; } = new(); // key aliases in SourceQuery ("__k0", "__k1"); empty = single-transaction load
    public List<ColumnBinding> Columns { get; set; } = new();
    public bool IdentityInsert { get; set; }
    public string? StagingDdl { get; set; }               // staging_merge only: "CREATE TABLE #stg (...)"
    public string? MergeSql { get; set; }                 // staging_merge only: moves #stg rows into Target
    public List<string> PreSql { get; set; } = new();
    public List<string> PostSql { get; set; } = new();
    public string CountSql { get; set; } = "";            // "SELECT COUNT_BIG(*) FROM (\n<SourceQuery>\n) AS q"
    public List<string> DependsOn { get; set; } = new();  // FK-parent task ids (cycle edges excluded)
    public int? ChunkSize { get; set; }                   // generator sets 5000 when any mapped column is LOB/(max)
    public bool Custom { get; set; }                      // true once an agent/human edited this task's SQL
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();     // from the last validation
    public string? MappingHash { get; set; }              // M4 addition: SHA-256 (lower hex) of the TableMap JSON this task was generated from
}

/// <summary>Source = alias in SourceQuery (equals the target column name for generated tasks); Target = target column name.</summary>
public sealed record ColumnBinding(string Source, string Target);

/// <summary>Result of SqlValidator.ValidateAsync (T4.3). Dictionaries contain an entry (possibly empty) for every validated task.</summary>
public sealed record ValidationReport(bool Ok, Dictionary<string, List<string>> TaskErrors, Dictionary<string, List<string>> TaskWarnings, List<string> GlobalErrors);
```

`plugins/db-migrate/engine/Dbm/Core/SqlGen/TopoSort.cs`:

```csharp
namespace Dbm.Core.SqlGen;

/// <summary>Order = parents first. Cycles = the cyclic strongly connected components found in the input (members sorted ordinally).
/// CycleEdges = the (child, parent) edges removed to break them.</summary>
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.SqlGen.TopoSortTests|FullyQualifiedName~Dbm.Tests.Unit.SqlGen.SqlQuoteTests"`
Expected: `Passed!  - Failed: 0, Passed: 14` (9 TopoSort + 5 SqlQuote).

- [ ] **Step 5: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/Sql/SqlQuote.cs plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlPlanPayload.cs plugins/db-migrate/engine/Dbm/Core/SqlGen/TopoSort.cs plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/TopoSortTests.cs
git commit -F - <<'EOF'
feat(sqlgen): add SqlQuote, SQL plan payload and deterministic topological sort

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 4.2: SQL generator

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlTypeText.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlGenerator.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/SqlGeneratorTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/SqlTypeTextTests.cs`

**Interfaces:**
- Consumes: `MappingPayload`, `TableMap`, `ColumnMap` (C12); `CatalogSnapshot`, `TableInfo`, `ColumnInfo`, `ForeignKeyInfo` (C10: `FindTable`, `FindColumn`, `BestKey()`, `Key`, `RefKey`); `ColumnType.From(ColumnInfo)` (C11); `Json.Serialize` (C1); `SqlQuote`, `TopoSort`, `SqlPlanPayload`/`TaskPlan`/`ColumnBinding` (T4.1). Tests: `Dbm.Tests.Support.SampleCatalogs.Source()/Target()`, `Dbm.Tests.Support.SampleMappings.Approved()` (T3.2).
- Produces:
  - `public static class SqlGenerator { SqlPlanPayload Generate(MappingPayload mapping, CatalogSnapshot src, CatalogSnapshot tgt, SqlPlanPayload? carryOver = null); string CountSql(string sourceQuery); string MappingHash(TableMap map); const int LobChunkSize = 5000; const string NoKeyWarning = "no key: single-transaction load"; const string ReviewMergeWarning = "review merge SQL"; const string CarriedWarning = "custom SQL carried over from the previous plan"; }`
  - `public static class SqlTypeText { string Format(ColumnType t); string Format(ColumnInfo c); bool IsLob(ColumnType t); bool IsLob(ColumnInfo c); ColumnType Parse(string systemTypeName, int precision, int scale); }`

**Generation rules (what the tests pin down):**
1. One task per TableMap whose `Kind != "skip"` and whose key is a table of the target catalog (unknown targets → plan warning, no task).
2. Edges = target FKs between mapped targets (child → parent). `TopoSort.Sort(..., preferBreak)` where an edge is preferred when every child-side FK column of every FK on that edge is nullable. Task ids `T01…Tnn` follow the resulting order (width grows to 3 digits past 99 tasks).
3. `SourceQuery` = `SELECT` + one line per bound target column in target ordinal order (`<Expr ?? Default> AS [Col]`; computed/rowversion targets are skipped with a warning) + one line per column of the primary source's `BestKey()` (`s.[k] AS [__kN]`), each line indented 4 spaces and comma-terminated except the last, then `FROM <From ?? [schema].[table] AS s>` and optional `WHERE (<Filter>)`; lines joined with `\n`; no `ORDER BY`.
4. `Columns` = one binding per selected target column (source alias = target name); `IdentityInsert` when a bound target column is identity; `ChunkSize = 5000` when a bound target column is LOB/(max) or a referenced source column (`ColumnMap.SourceColumns`) is; `CountSql = SqlGenerator.CountSql(SourceQuery)`; `DependsOn` = parent task ids of non-cycle edges (sorted); `MappingHash` = SHA-256 of `Json.Serialize(tableMap)`.
5. `Kind == "lookup"` → `Mode = "staging_merge"`, `StagingDdl` = `CREATE TABLE #stg (` + bound columns with target types (+ `COLLATE` when the catalog has one) + key aliases typed from the source key columns, all `NULL` + `);`, `MergeSql` = `INSERT INTO [s].[t] (cols)\nSELECT cols\nFROM #stg;`, warning `review merge SQL`.
6. Warnings: `"<Col>: <TypeRisk>"` per bound column with a mapping TypeRisk, generator notes, `no key: single-transaction load`, `target has N trigger(s); not fired unless FireTriggers`, `review merge SQL`, `custom SQL carried over from the previous plan`.
7. Cycle edges → global `PreSql` `ALTER TABLE [s].[t] NOCHECK CONSTRAINT [fk];` and `PostSql` `ALTER TABLE [s].[t] WITH CHECK CHECK CONSTRAINT [fk];` per FK on the edge, plus a plan warning.
8. Carry-over: a `carryOver` task with `Custom = true`, the same `Target` (case-insensitive) and the same `MappingHash` keeps `Mode`, `SourceQuery`, `StagingDdl`, `MergeSql`, `PreSql`, `PostSql`, `ChunkSize`, `KeyColumns`, `Columns`, `IdentityInsert`; `CountSql` is recomputed and `Custom` stays true. Global pre/post are always regenerated.

- [ ] **Step 1: Write the failing tests**

`plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/SqlGeneratorTests.cs`:

```csharp
using Dbm.Core;
using Dbm.Core.SqlGen;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Unit.SqlGen;

public class SqlGeneratorTests
{
    static SqlPlanPayload Plan() => SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());

    static TaskPlan Task(SqlPlanPayload plan, string target) => plan.Tasks.Values.Single(t => t.Target == target);

    static string IdOf(SqlPlanPayload plan, string target) => plan.Tasks.Single(kv => kv.Value.Target == target).Key;

    [Fact]
    public void Tasks_are_numbered_in_dependency_order()
    {
        var plan = Plan();
        Assert.Equal(["T01", "T02", "T03", "T04", "T05", "T06"], plan.Order);
        Assert.Equal(["app.AuditEvents", "app.Customers", "app.Products", "app.Addresses", "app.Orders", "app.OrderLines"],
            plan.Order.Select(id => plan.Tasks[id].Target));
        int Pos(string t) => plan.Order.IndexOf(IdOf(plan, t));
        Assert.True(Pos("app.Products") < Pos("app.Addresses") && Pos("app.Customers") < Pos("app.Addresses"));
        Assert.True(Pos("app.Orders") > Pos("app.Customers") && Pos("app.Orders") > Pos("app.Addresses"));
        Assert.True(Pos("app.OrderLines") > Pos("app.Orders") && Pos("app.OrderLines") > Pos("app.Products"));
    }

    [Fact]
    public void DependsOn_excludes_the_cycle_edge()
    {
        var plan = Plan();
        Assert.Empty(Task(plan, "app.Customers").DependsOn);
        Assert.Equal([IdOf(plan, "app.Customers")], Task(plan, "app.Addresses").DependsOn);
        Assert.Equal(["T02", "T04"], Task(plan, "app.Orders").DependsOn);
        Assert.Equal(["T03", "T05"], Task(plan, "app.OrderLines").DependsOn);
        Assert.Empty(Task(plan, "app.AuditEvents").DependsOn);
    }

    [Fact]
    public void Addresses_source_query_is_exact()
    {
        const string expected =
            "SELECT\n" +
            "    s.[ADDR_ID] AS [AddressId],\n" +
            "    s.[CUST_ID] AS [CustomerId],\n" +
            "    s.[LINE1] AS [Line1],\n" +
            "    s.[CITY] AS [City],\n" +
            "    s.[ZIP] AS [PostalCode],\n" +
            "    s.[CTRY_CD] AS [CountryCode],\n" +
            "    s.[ADDR_ID] AS [__k0]\n" +
            "FROM [dbo].[ADDR] AS s";
        var task = Task(Plan(), "app.Addresses");
        Assert.Equal(expected, task.SourceQuery);
        Assert.Equal("SELECT COUNT_BIG(*) FROM (\n" + expected + "\n) AS q", task.CountSql);
        Assert.Equal(["__k0"], task.KeyColumns);
        Assert.Equal(["AddressId", "CustomerId", "Line1", "City", "PostalCode", "CountryCode"], task.Columns.Select(c => c.Target));
        Assert.All(task.Columns, c => Assert.Equal(c.Target, c.Source));
        Assert.Equal("direct", task.Mode);
    }

    [Fact]
    public void Orders_source_query_uses_the_merge_from_clause()
    {
        const string expected =
            "SELECT\n" +
            "    s.[ORD_ID] AS [OrderId],\n" +
            "    s.[CUST_ID] AS [CustomerId],\n" +
            "    s.[ORD_DT] AS [OrderDate],\n" +
            "    st.[STATUS_CD] AS [StatusCode],\n" +
            "    s.[SHIP_ADDR_ID] AS [ShippingAddressId],\n" +
            "    s.[TOTAL_AMT] AS [TotalAmount],\n" +
            "    s.[CMNT] AS [Comment],\n" +
            "    s.[ORD_ID] AS [__k0]\n" +
            "FROM [dbo].[ORD_HDR] AS s JOIN [dbo].[ORD_STATUS] AS st ON st.[STATUS_ID] = s.[STATUS_ID]";
        var task = Task(Plan(), "app.Orders");
        Assert.Equal(expected, task.SourceQuery);
        Assert.Contains("target has 1 trigger(s); not fired unless FireTriggers", task.Warnings);
    }

    [Fact]
    public void OrderLines_has_a_composite_key()
    {
        var task = Task(Plan(), "app.OrderLines");
        Assert.Equal(["__k0", "__k1"], task.KeyColumns);
        Assert.EndsWith("    s.[ORD_ID] AS [__k0],\n    s.[LINE_NO] AS [__k1]\nFROM [dbo].[ORD_LINE] AS s", task.SourceQuery);
    }

    [Fact]
    public void Cycle_constraint_is_disabled_before_and_rechecked_after()
    {
        var plan = Plan();
        Assert.Equal(["ALTER TABLE [app].[Customers] NOCHECK CONSTRAINT [FK_Customers_PrimaryAddress];"], plan.PreSql);
        Assert.Equal(["ALTER TABLE [app].[Customers] WITH CHECK CHECK CONSTRAINT [FK_Customers_PrimaryAddress];"], plan.PostSql);
        Assert.Contains(plan.Warnings, w => w.StartsWith("FK cycle broken at app.Customers -> app.Addresses", StringComparison.Ordinal));
    }

    [Fact]
    public void IdentityInsert_follows_bound_identity_columns()
    {
        var plan = Plan();
        Assert.True(Task(plan, "app.Customers").IdentityInsert);
        Assert.True(Task(plan, "app.Addresses").IdentityInsert);
        Assert.True(Task(plan, "app.Products").IdentityInsert);
        Assert.True(Task(plan, "app.Orders").IdentityInsert);
        Assert.False(Task(plan, "app.OrderLines").IdentityInsert);
        Assert.False(Task(plan, "app.AuditEvents").IdentityInsert);
    }

    [Fact]
    public void Heap_source_has_no_keys_and_a_warning()
    {
        var task = Task(Plan(), "app.AuditEvents");
        Assert.Empty(task.KeyColumns);
        Assert.Contains(SqlGenerator.NoKeyWarning, task.Warnings);
        Assert.DoesNotContain("__k", task.SourceQuery);
    }

    [Fact]
    public void Computed_and_rowversion_columns_are_never_bound()
    {
        var plan = Plan();
        Assert.DoesNotContain(Task(plan, "app.Customers").Columns, c => c.Target == "DisplayName");
        Assert.DoesNotContain(Task(plan, "app.Products").Columns, c => c.Target == "RowVer");
    }

    [Fact]
    public void Lob_columns_get_a_small_chunk_size()
    {
        var plan = Plan();
        Assert.Equal(SqlGenerator.LobChunkSize, Task(plan, "app.Customers").ChunkSize);
        Assert.Null(Task(plan, "app.Addresses").ChunkSize);
    }

    [Fact]
    public void Type_risk_from_the_mapping_becomes_a_warning()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Orders"].Columns["Comment"].TypeRisk = "varchar(500) -> nvarchar(200) may truncate";
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target());
        Assert.Contains("Comment: varchar(500) -> nvarchar(200) may truncate", Task(plan, "app.Orders").Warnings);
    }

    [Fact]
    public void Lookup_maps_become_staging_merge_tasks()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";
        var task = Task(SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target()), "app.Addresses");
        Assert.Equal("staging_merge", task.Mode);
        Assert.StartsWith("CREATE TABLE #stg (\n    [AddressId] int NULL,\n    [CustomerId] int NULL,\n    [Line1] nvarchar(200)", task.StagingDdl);
        Assert.Contains("\n    [__k0] int NULL\n);", task.StagingDdl);
        Assert.Equal(
            "INSERT INTO [app].[Addresses] ([AddressId], [CustomerId], [Line1], [City], [PostalCode], [CountryCode])\n" +
            "SELECT [AddressId], [CustomerId], [Line1], [City], [PostalCode], [CountryCode]\n" +
            "FROM #stg;", task.MergeSql);
        Assert.Contains(SqlGenerator.ReviewMergeWarning, task.Warnings);
    }

    [Fact]
    public void Filter_becomes_a_parenthesised_where_clause()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Filter = "s.[CTRY_CD] <> 'XX'";
        var task = Task(SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target()), "app.Addresses");
        Assert.EndsWith("\nFROM [dbo].[ADDR] AS s\nWHERE (s.[CTRY_CD] <> 'XX')", task.SourceQuery);
    }

    [Fact]
    public void Skip_maps_produce_no_task()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.AuditEvents"].Kind = "skip";
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target());
        Assert.Equal(5, plan.Tasks.Count);
        Assert.DoesNotContain(plan.Tasks.Values, t => t.Target == "app.AuditEvents");
        Assert.Equal("app.Customers", plan.Tasks["T01"].Target);
    }

    [Fact]
    public void Custom_task_with_unchanged_mapping_is_carried_over()
    {
        var first = Plan();
        var addresses = Task(first, "app.Addresses");
        addresses.SourceQuery = addresses.SourceQuery + "\nWHERE s.[CITY] <> ''";
        addresses.PostSql.Add("UPDATE STATISTICS [app].[Addresses];");
        addresses.Custom = true;

        var second = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target(), first);
        var carried = Task(second, "app.Addresses");
        Assert.True(carried.Custom);
        Assert.Equal(addresses.SourceQuery, carried.SourceQuery);
        Assert.Equal(["UPDATE STATISTICS [app].[Addresses];"], carried.PostSql);
        Assert.Equal(SqlGenerator.CountSql(addresses.SourceQuery), carried.CountSql);
        Assert.Contains(SqlGenerator.CarriedWarning, carried.Warnings);
        Assert.False(Task(second, "app.Orders").Custom);
    }

    [Fact]
    public void Custom_task_is_regenerated_when_its_mapping_changed()
    {
        var first = Plan();
        var addresses = Task(first, "app.Addresses");
        addresses.SourceQuery = "SELECT 1 AS [AddressId]";
        addresses.Custom = true;

        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Columns["City"].Expr = "UPPER(s.[CITY])";
        var second = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target(), first);
        var task = Task(second, "app.Addresses");
        Assert.False(task.Custom);
        Assert.Contains("    UPPER(s.[CITY]) AS [City],", task.SourceQuery);
        Assert.NotEqual(addresses.MappingHash, task.MappingHash);
    }

    [Fact]
    public void Plan_round_trips_through_json_with_camel_case_names()
    {
        var plan = Plan();
        var json = Json.Serialize(plan);
        Assert.Contains("\"sourceQuery\":", json);
        Assert.Contains("\"mappingHash\":", json);
        var back = Json.Deserialize<SqlPlanPayload>(json);
        Assert.Equal(plan.Order, back.Order);
        Assert.Equal(plan.Tasks["T05"].SourceQuery, back.Tasks["T05"].SourceQuery);
        Assert.Equal(plan.Tasks["T05"].Columns, back.Tasks["T05"].Columns);
        Assert.Matches("^[0-9a-f]{64}$", back.Tasks["T05"].MappingHash!);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/SqlTypeTextTests.cs`:

```csharp
using Dbm.Core.Matching;
using Dbm.Core.SqlGen;
using Xunit;

namespace Dbm.Tests.Unit.SqlGen;

public class SqlTypeTextTests
{
    [Theory]
    [InlineData("varchar(100)", 0, 0, "varchar", 100, 0, 0)]
    [InlineData("nvarchar(max)", 0, 0, "nvarchar", -1, 0, 0)]
    [InlineData("decimal(19,4)", 19, 4, "decimal", 0, 19, 4)]
    [InlineData("datetime2(0)", 19, 0, "datetime2", 0, 19, 0)]
    [InlineData("int", 10, 0, "int", 0, 10, 0)]
    [InlineData("xml", 0, 0, "xml", -1, 0, 0)]
    public void Parse_reads_system_type_names(string name, int precision, int scale, string dataType, int maxLength, int p, int s) =>
        Assert.Equal(new ColumnType(dataType, maxLength, p, s), SqlTypeText.Parse(name, precision, scale));

    [Fact]
    public void Format_writes_tsql_type_names()
    {
        Assert.Equal("nvarchar(max)", SqlTypeText.Format(new ColumnType("nvarchar", -1, 0, 0)));
        Assert.Equal("decimal(19,4)", SqlTypeText.Format(new ColumnType("decimal", 0, 19, 4)));
        Assert.Equal("datetime2(3)", SqlTypeText.Format(new ColumnType("datetime2", 0, 23, 3)));
        Assert.Equal("int", SqlTypeText.Format(new ColumnType("int", 0, 10, 0)));
    }

    [Fact]
    public void IsLob_covers_max_and_legacy_types()
    {
        Assert.True(SqlTypeText.IsLob(new ColumnType("text", 0, 0, 0)));
        Assert.True(SqlTypeText.IsLob(new ColumnType("nvarchar", -1, 0, 0)));
        Assert.False(SqlTypeText.IsLob(new ColumnType("nvarchar", 50, 0, 0)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.SqlGen.SqlGeneratorTests|FullyQualifiedName~Dbm.Tests.Unit.SqlGen.SqlTypeTextTests"`
Expected: build FAILS with `error CS0103: The name 'SqlGenerator' does not exist in the current context` and `CS0103: The name 'SqlTypeText' does not exist in the current context`.

- [ ] **Step 3: Implement**

`plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlTypeText.cs`:

```csharp
using Dbm.Core.Catalog;
using Dbm.Core.Matching;

namespace Dbm.Core.SqlGen;

/// <summary>Formats catalog column types as T-SQL and parses <c>sp_describe_first_result_set</c> type names back into <see cref="ColumnType"/>.</summary>
public static class SqlTypeText
{
    static readonly HashSet<string> Lengthed = new(StringComparer.OrdinalIgnoreCase) { "char", "varchar", "nchar", "nvarchar", "binary", "varbinary" };
    static readonly HashSet<string> PrecisionScale = new(StringComparer.OrdinalIgnoreCase) { "decimal", "numeric" };
    static readonly HashSet<string> ScaleOnly = new(StringComparer.OrdinalIgnoreCase) { "datetime2", "time", "datetimeoffset" };
    static readonly HashSet<string> LegacyLob = new(StringComparer.OrdinalIgnoreCase) { "text", "ntext", "image", "xml" };

    /// <summary>"nvarchar(50)", "varchar(max)", "decimal(19,4)", "datetime2(0)", "int".</summary>
    public static string Format(ColumnType t)
    {
        var dt = t.DataType.ToLowerInvariant();
        if (Lengthed.Contains(dt)) return $"{dt}({(t.MaxLength == -1 ? "max" : t.MaxLength.ToString())})";
        if (PrecisionScale.Contains(dt)) return $"{dt}({t.Precision},{t.Scale})";
        if (ScaleOnly.Contains(dt)) return $"{dt}({t.Scale})";
        return dt;
    }

    public static string Format(ColumnInfo c) => Format(ColumnType.From(c));

    /// <summary>(max) types and the legacy LOB types text / ntext / image / xml.</summary>
    public static bool IsLob(ColumnType t) => t.MaxLength == -1 || LegacyLob.Contains(t.DataType);

    public static bool IsLob(ColumnInfo c) => IsLob(ColumnType.From(c));

    /// <summary>Parses a <c>system_type_name</c> such as "varchar(100)", "nvarchar(max)", "decimal(19,4)" or "int".
    /// Length is taken from the name (characters for n/char types, bytes for binary types, -1 for max);
    /// precision and scale come from the result-set columns of the same name (same meaning as sys.columns).</summary>
    public static ColumnType Parse(string systemTypeName, int precision, int scale)
    {
        var name = systemTypeName.Trim().ToLowerInvariant();
        var paren = name.IndexOf('(');
        var dataType = paren < 0 ? name : name[..paren].Trim();
        var args = paren < 0 ? "" : name[(paren + 1)..].TrimEnd(')').Trim();
        var maxLength = 0;
        if (Lengthed.Contains(dataType))
            maxLength = args == "max" ? -1 : int.TryParse(args, out var n) ? n : 0;
        else if (string.Equals(dataType, "xml", StringComparison.Ordinal))
            maxLength = -1;
        return new ColumnType(dataType, maxLength, Lengthed.Contains(dataType) ? 0 : precision, Lengthed.Contains(dataType) ? 0 : scale);
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlGenerator.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.Sql;

namespace Dbm.Core.SqlGen;

/// <summary>Turns an approved mapping into a migration plan: one task per non-skip TableMap, ordered parents-first.</summary>
public static class SqlGenerator
{
    public const int LobChunkSize = 5000;
    public const string NoKeyWarning = "no key: single-transaction load";
    public const string ReviewMergeWarning = "review merge SQL";
    public const string CarriedWarning = "custom SQL carried over from the previous plan";

    public static SqlPlanPayload Generate(MappingPayload mapping, CatalogSnapshot src, CatalogSnapshot tgt, SqlPlanPayload? carryOver = null)
    {
        var plan = new SqlPlanPayload();

        // 1. Mapped targets (exact catalog case), skipping "skip" maps and unknown tables.
        var maps = new Dictionary<string, (TableInfo Table, TableMap Map)>(StringComparer.Ordinal);
        foreach (var (key, map) in mapping.Tables.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (string.Equals(map.Kind, "skip", StringComparison.OrdinalIgnoreCase)) continue;
            var table = tgt.FindTable(key);
            if (table is null) { plan.Warnings.Add($"{key}: target table not found in the target catalog; no task generated"); continue; }
            maps[table.Key] = (table, map);
        }

        // 2. FK edges (child -> parent) among mapped targets.
        var fksByEdge = new Dictionary<(string Child, string Parent), List<ForeignKeyInfo>>();
        foreach (var (key, (table, _)) in maps)
            foreach (var fk in table.ForeignKeys)
            {
                var parent = tgt.FindTable(fk.RefKey)?.Key;
                if (parent is null || !maps.ContainsKey(parent)) continue;
                if (!fksByEdge.TryGetValue((key, parent), out var list)) fksByEdge[(key, parent)] = list = new List<ForeignKeyInfo>();
                list.Add(fk);
            }
        var topo = TopoSort.Sort(maps.Keys.ToList(), fksByEdge.Keys.ToList(),
            (child, parent) => fksByEdge[(child, parent)].All(fk => fk.Columns.All(c => maps[child].Table.FindColumn(c)?.IsNullable == true)));
        var cycleEdges = new HashSet<(string Child, string Parent)>(topo.CycleEdges);

        // 3. Task ids in execution order.
        var width = Math.Max(2, topo.Order.Count.ToString().Length);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < topo.Order.Count; i++) ids[topo.Order[i]] = "T" + (i + 1).ToString("D" + width);

        // 4. Tasks.
        foreach (var target in topo.Order)
        {
            var (table, map) = maps[target];
            var task = BuildTask(table, map, src, out var notes);
            task.DependsOn = fksByEdge.Keys
                .Where(e => e.Child == target && e.Parent != target && !cycleEdges.Contains(e))
                .Select(e => ids[e.Parent]).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var old = carryOver?.Tasks.Values.FirstOrDefault(p => p.Custom
                && string.Equals(p.Target, task.Target, StringComparison.OrdinalIgnoreCase)
                && p.MappingHash is not null && p.MappingHash == task.MappingHash);
            if (old is not null) CarryOver(task, old);
            task.Warnings = BuildWarnings(task, table, map, notes);
            plan.Tasks[ids[target]] = task;
            plan.Order.Add(ids[target]);
        }

        // 5. FK cycles: disable the cut constraints for the load, re-enable WITH CHECK afterwards.
        foreach (var edge in topo.CycleEdges)
        {
            var child = maps[edge.Child].Table;
            var tableSql = SqlQuote.Table(child.Schema, child.Name);
            foreach (var fk in fksByEdge[edge].OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                plan.PreSql.Add($"ALTER TABLE {tableSql} NOCHECK CONSTRAINT {SqlQuote.Ident(fk.Name)};");
                plan.PostSql.Add($"ALTER TABLE {tableSql} WITH CHECK CHECK CONSTRAINT {SqlQuote.Ident(fk.Name)};");
            }
            plan.Warnings.Add($"FK cycle broken at {edge.Child} -> {edge.Parent}: constraint(s) disabled during the load and re-enabled WITH CHECK afterwards");
        }
        return plan;
    }

    /// <summary>The row-count query for a source query. The newline before ")" keeps a trailing "--" comment harmless.</summary>
    public static string CountSql(string sourceQuery) => "SELECT COUNT_BIG(*) FROM (\n" + sourceQuery.TrimEnd() + "\n) AS q";

    /// <summary>Lower-hex SHA-256 of the TableMap serialised with Json.Options (compact).</summary>
    public static string MappingHash(TableMap map) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Serialize(map)))).ToLowerInvariant();

    static TaskPlan BuildTask(TableInfo table, TableMap map, CatalogSnapshot src, out List<string> notes)
    {
        notes = new List<string>();
        var task = new TaskPlan
        {
            Target = table.Key,
            Mode = string.Equals(map.Kind, "lookup", StringComparison.OrdinalIgnoreCase) ? "staging_merge" : "direct",
            MappingHash = MappingHash(map),
        };

        var selectLines = new List<string>();
        var bound = new List<ColumnInfo>();
        var referencesLob = false;
        foreach (var col in table.Columns.OrderBy(c => c.Ordinal))
        {
            var cm = FindColumnMap(map, col.Name);
            if (cm is null) continue;
            var expr = !string.IsNullOrWhiteSpace(cm.Expr) ? cm.Expr.Trim()
                : !string.IsNullOrWhiteSpace(cm.Default) ? cm.Default.Trim() : null;
            if (expr is null) continue;
            if (col.IsComputed || col.IsRowVersion)
            {
                notes.Add($"{col.Name}: target column is computed or rowversion; its mapping is ignored");
                continue;
            }
            selectLines.Add($"{expr} AS {SqlQuote.Ident(col.Name)}");
            bound.Add(col);
            task.Columns.Add(new ColumnBinding(col.Name, col.Name));
            referencesLob |= cm.SourceColumns.Any(sc => FindSourceColumn(src, sc) is { } s && SqlTypeText.IsLob(s));
        }

        TableInfo? primary = map.Sources.Count > 0 ? src.FindTable(map.Sources[0]) : null;
        if (map.Sources.Count > 0 && primary is null) notes.Add($"primary source {map.Sources[0]} not found in the source catalog");
        var keyColumns = primary?.BestKey() ?? [];
        var keyLines = new List<string>();
        var keyDdl = new List<string>();
        for (var i = 0; i < keyColumns.Count; i++)
        {
            var alias = "__k" + i;
            keyLines.Add($"s.{SqlQuote.Ident(keyColumns[i])} AS {SqlQuote.Ident(alias)}");
            task.KeyColumns.Add(alias);
            var keyCol = primary!.FindColumn(keyColumns[i]);
            keyDdl.Add($"    {SqlQuote.Ident(alias)} {(keyCol is null ? "sql_variant" : TypeWithCollation(keyCol))} NULL");
        }

        var from = !string.IsNullOrWhiteSpace(map.From) ? map.From.Trim()
            : primary is not null ? SqlQuote.Table(primary.Schema, primary.Name) + " AS s"
            : map.Sources.Count > 0 ? SqlQuote.TableKey(map.Sources[0]) + " AS s"
            : null;
        if (from is null) notes.Add("no source table: the query returns a single row");

        var sb = new StringBuilder("SELECT");
        var items = selectLines.Concat(keyLines).ToList();
        for (var i = 0; i < items.Count; i++)
            sb.Append("\n    ").Append(items[i]).Append(i < items.Count - 1 ? "," : "");
        if (from is not null) sb.Append("\nFROM ").Append(from);
        if (!string.IsNullOrWhiteSpace(map.Filter)) sb.Append("\nWHERE (").Append(map.Filter.Trim()).Append(')');
        task.SourceQuery = sb.ToString();
        task.CountSql = CountSql(task.SourceQuery);
        task.IdentityInsert = bound.Any(c => c.IsIdentity);
        if (referencesLob || bound.Any(SqlTypeText.IsLob)) task.ChunkSize = LobChunkSize;

        if (task.Mode == "staging_merge")
        {
            var ddl = bound.Select(c => $"    {SqlQuote.Ident(c.Name)} {TypeWithCollation(c)} NULL").Concat(keyDdl);
            task.StagingDdl = "CREATE TABLE #stg (\n" + string.Join(",\n", ddl) + "\n);";
            var cols = string.Join(", ", bound.Select(c => SqlQuote.Ident(c.Name)));
            task.MergeSql = $"INSERT INTO {SqlQuote.Table(table.Schema, table.Name)} ({cols})\nSELECT {cols}\nFROM #stg;";
        }
        return task;
    }

    static void CarryOver(TaskPlan task, TaskPlan old)
    {
        task.Mode = old.Mode;
        task.SourceQuery = old.SourceQuery;
        task.StagingDdl = old.StagingDdl;
        task.MergeSql = old.MergeSql;
        task.PreSql = old.PreSql.ToList();
        task.PostSql = old.PostSql.ToList();
        task.ChunkSize = old.ChunkSize;
        task.KeyColumns = old.KeyColumns.ToList();
        task.Columns = old.Columns.ToList();
        task.IdentityInsert = old.IdentityInsert;
        task.CountSql = CountSql(task.SourceQuery);
        task.Custom = true;
    }

    static List<string> BuildWarnings(TaskPlan task, TableInfo table, TableMap map, List<string> notes)
    {
        var w = new List<string>();
        foreach (var binding in task.Columns)
            if (FindColumnMap(map, binding.Target) is { TypeRisk: { Length: > 0 } risk })
                w.Add($"{binding.Target}: {risk}");
        w.AddRange(notes);
        if (task.KeyColumns.Count == 0) w.Add(NoKeyWarning);
        if (table.TriggerCount > 0) w.Add($"target has {table.TriggerCount} trigger(s); not fired unless FireTriggers");
        if (task.Mode == "staging_merge" && !task.Custom) w.Add(ReviewMergeWarning);
        if (task.Custom) w.Add(CarriedWarning);
        return w.Distinct(StringComparer.Ordinal).ToList();
    }

    static ColumnMap? FindColumnMap(TableMap map, string column)
    {
        if (map.Columns.TryGetValue(column, out var exact)) return exact;
        foreach (var (k, v) in map.Columns)
            if (string.Equals(k, column, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    /// <summary>"schema.table.column" → the catalog column (split on the last '.').</summary>
    static ColumnInfo? FindSourceColumn(CatalogSnapshot src, string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        if (dot <= 0) return null;
        return src.FindTable(qualified[..dot])?.FindColumn(qualified[(dot + 1)..]);
    }

    static string TypeWithCollation(ColumnInfo c) =>
        string.IsNullOrEmpty(c.Collation) ? SqlTypeText.Format(c) : $"{SqlTypeText.Format(c)} COLLATE {c.Collation}";
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.SqlGen.SqlGeneratorTests|FullyQualifiedName~Dbm.Tests.Unit.SqlGen.SqlTypeTextTests"`
Expected: `Passed!  - Failed: 0, Passed: 25` (17 generator + 8 type-text).

If `Addresses_source_query_is_exact` fails only on the expression text, check `SampleMappings.Approved()` (T3.2): simple columns must be `s.[COL]` per the C15 ground truth — fix the sample mapping there, not the expected string here.

- [ ] **Step 5: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlTypeText.cs plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlGenerator.cs plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/SqlGeneratorTests.cs plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/SqlTypeTextTests.cs
git commit -F - <<'EOF'
feat(sqlgen): generate ordered migration tasks from the approved mapping

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 4.3: SQL validator, sqlgen job, `sql` commands

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlValidator.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlPlanSource.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlGenJob.cs`
- Create: `plugins/db-migrate/engine/Dbm/Cli/Commands/SqlCommands.cs`
- Modify: `plugins/db-migrate/engine/Dbm/Core/JobRegistry.cs` (T1.6) — one line
- Modify: `plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs` (T0.1) — two lines
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/SqlValidatorUnitTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/SqlCommandsTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Integration/SqlGen/SqlValidatorTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Integration/SqlGen/SqlGenJobTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Integration/SqlGen/SqlCommandsIntegrationTests.cs`

**Interfaces:**
- Consumes: `SqlConnect.OpenAsync`, `Redactor.Scrub/SecretsOf` (C3); `TypeCompat.Check`, `ColumnType.From`, `CompatLevel` (T3.1); `CatalogSnapshot.FindTable`, `TableInfo.FindColumn` (T2.2); `DbmServices` (`Db`, `Phases`, `Artifacts`, `Catalog`, `Connections`, `Jobs`, `Project`, `Workflow`), `PhaseName`, `PhaseStatus`, `JobStatus`, `Side` (C2/C6); `IJobHandler`, `JobContext`, `JobResult`, `JobRunner.RunPendingAsync` (T1.6); `WorkflowEngine.RetryJob` (T1.5); `Patch.Parse`, `JsonPatch.Apply`, `PatchException` (T1.4); `ICommand`, `Args`, `CliContext.OpenProject()`, `CliFailure`, `Output.Ok/Fail/Write`, `EnumText`, `Json` (T0.1/T1.8); `SqlGenerator`, `SqlTypeText` (T4.2). Tests: `TestWorkspace.OpenServices()`, `CliRunner.RunAsync(ws, null, args…)` (M1); `SampleDatabases.CreateAsync(1, seed: false)`, `SamplePair.SourceCs/TargetCs`, `SampleProject.CreateAsync(pair)` (connections probed and saved), `TempProject.Create()` (T2.1); `SampleCatalogs`, `SampleMappings` (T3.2).
- Produces:
  - `public static class SqlValidator { Task<ValidationReport> ValidateAsync(SqlPlanPayload plan, string sourceCs, string targetCs, CatalogSnapshot tgt, string? onlyTaskId, CancellationToken ct); List<string> CheckShape(TaskPlan task); void Apply(SqlPlanPayload plan, ValidationReport report, bool full = true); Task<(List<ResultColumn>? Columns, string? Error)> DescribeAsync(SqlConnection conn, string sql, CancellationToken ct); Task<string?> ParseCheckAsync(SqlConnection conn, string sql, CancellationToken ct); const string WarningPrefix = "validate: "; sealed record ResultColumn(string Name, string SystemTypeName, bool IsNullable, int Precision, int Scale); }`
  - `public static class SqlPlanSource { MappingPayload? ApprovedMapping(DbmServices s); ArtifactRow? CurrentRow(DbmServices s); bool CanValidate(DbmServices s); Task<ValidationReport> ValidateLiveAsync(DbmServices s, SqlPlanPayload plan, string? onlyTaskId, CancellationToken ct); }`
  - `public sealed class SqlGenJob : IJobHandler` (`Kind = "sqlgen"`) with `public static string Summary(SqlPlanPayload plan)` → `"6 tasks, 0 errors, 9 warnings"`.
  - `dbm sql gen [--inline]` (`SqlGenCommand`): only while Sql is `running` or `drafting` (a drafting phase is put back to `running`, because `WorkflowEngine.OnJobDone` only accepts a job result for a running phase); queues `sqlgen` through `WorkflowEngine.RetryJob` unless a Sql job is already active → `{"ok":true,"queued":"sqlgen"}`; `--inline` also runs the queue in-process → `{"ok":true,"ran":1,"status":"awaiting_review","version":0,"summary":"6 tasks, 0 errors, 9 warnings"}`.
  - `dbm sql validate [--task <id>] [--patch <file>]` (`SqlValidateCommand`): `{"ok":…,"version":…,"patched":…,"taskErrors":{…},"taskWarnings":{…},"globalErrors":[…]}`, **exit 1 when `ok` is false** (like `dbm apply`). Nothing is stored.
  - Error codes: `no_project` (from `CliContext.OpenProject`), `wrong_phase`, `job_failed`, `no_version`, `not_found`, `stale_patch`, `invalid_patch`, `bad_payload`, `not_ready`.

**Validation rules:** per task — `CheckShape` (mode, staging fields, empty query, duplicate bindings, `GO` lines); target table in the catalog; every bound target column exists and is not computed/rowversion; identity bound vs `IdentityInsert` (warnings); on the SOURCE `sp_describe_first_result_set` (compile errors → `sourceQuery: <message>`; every binding source alias and key alias must be a result column; unnamed/duplicate result columns are errors; `TypeCompat` Incompatible → error, Risky → warning `"<Col>: <srcType> -> <tgtType>: <risk>"`; nullable result into NOT NULL target → warning); on the TARGET every task pre/post/staging/merge statement and (full validation only) every global pre/post statement is parse-checked with the three-batch PARSEONLY form. Connection failures become global errors (scrubbed of secrets). Nothing is ever executed on either database.

- [ ] **Step 1: Write the failing tests**

`plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/SqlValidatorUnitTests.cs`:

```csharp
using Dbm.Core.SqlGen;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Unit.SqlGen;

public class SqlValidatorUnitTests
{
    [Fact]
    public void CheckShape_reports_mode_staging_duplicates_and_go()
    {
        var task = new TaskPlan
        {
            Target = "app.Addresses",
            Mode = "staging_merge",
            SourceQuery = "SELECT 1 AS [City]\nGO",
            Columns = [new ColumnBinding("City", "City"), new ColumnBinding("City2", "city")],
        };
        var errors = SqlValidator.CheckShape(task);
        Assert.Contains("staging_merge requires stagingDdl", errors);
        Assert.Contains("staging_merge requires mergeSql", errors);
        Assert.Contains("City: bound more than once", errors);
        Assert.Contains("sourceQuery: GO batch separators are not allowed", errors);
    }

    [Fact]
    public void CheckShape_accepts_generated_tasks_and_go_inside_comments()
    {
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        Assert.All(plan.Tasks.Values, t => Assert.Empty(SqlValidator.CheckShape(t)));
        var task = plan.Tasks["T04"];
        task.PreSql.Add("-- GO live checklist\nUPDATE STATISTICS [app].[Addresses];");
        Assert.Empty(SqlValidator.CheckShape(task));
    }

    [Fact]
    public void Apply_keeps_generator_warnings_and_replaces_validator_warnings()
    {
        var plan = new SqlPlanPayload
        {
            Order = ["T01"],
            Tasks = { ["T01"] = new TaskPlan { Target = "app.X", Warnings = ["no key: single-transaction load", "validate: old"] } },
        };
        SqlValidator.Apply(plan, new ValidationReport(false, new() { ["T01"] = ["e1"] }, new() { ["T01"] = ["w1"] }, ["g1"]));
        Assert.Equal(["e1"], plan.Tasks["T01"].Errors);
        Assert.Equal(["no key: single-transaction load", "validate: w1"], plan.Tasks["T01"].Warnings);
        Assert.Equal(["g1"], plan.Errors);

        SqlValidator.Apply(plan, new ValidationReport(true, new() { ["T01"] = [] }, new() { ["T01"] = [] }, []), full: false);
        Assert.Empty(plan.Tasks["T01"].Errors);
        Assert.Equal(["no key: single-transaction load"], plan.Tasks["T01"].Warnings);
        Assert.Equal(["g1"], plan.Errors);
    }

    [Fact]
    public async Task Unknown_task_id_fails_before_connecting()
    {
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        var report = await SqlValidator.ValidateAsync(plan, "Server=nowhere;Database=x", "Server=nowhere;Database=y",
            SampleCatalogs.Target(), "T99", CancellationToken.None);
        Assert.False(report.Ok);
        Assert.Equal(["unknown task T99"], report.GlobalErrors);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/SqlCommandsTests.cs` (runs through `CliApp` with `CliRunner`, so `CliFailure` becomes the `{"error",…}` line exactly as for a user):

```csharp
using Dbm.Cli.Commands;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Cli;

public sealed class SqlCommandsTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Commands_use_the_contract_names()
    {
        Assert.Equal("sql gen", new SqlGenCommand().Name);
        Assert.Equal("sql validate", new SqlValidateCommand().Name);
    }

    [Fact]
    public async Task Gen_is_refused_outside_the_sql_phase()
    {
        _workspace.OpenServices();
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "gen");
        Assert.Equal(1, r.Exit);
        Assert.Equal("wrong_phase", (string?)r.Json["error"]);
    }

    [Fact]
    public async Task Gen_while_drafting_requeues_the_job_and_reruns_the_phase()
    {
        var s = _workspace.OpenServices();
        s.Phases.SetStatus(PhaseName.Sql, PhaseStatus.Drafting);
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "gen");
        Assert.Equal(0, r.Exit);
        Assert.Equal("sqlgen", (string?)r.Json["queued"]);
        Assert.Equal(PhaseStatus.Running, s.Phases.Get(PhaseName.Sql).Status);
        Assert.Contains(s.Jobs.Active(), j => j.Kind == "sqlgen" && j.Phase == PhaseName.Sql);
    }

    [Fact]
    public async Task Validate_needs_a_version()
    {
        _workspace.OpenServices();
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate", "--task", "T01");
        Assert.Equal(1, r.Exit);
        Assert.Equal("no_version", (string?)r.Json["error"]);
    }

    [Fact]
    public async Task Missing_project_is_reported()
    {
        var r = await CliRunner.RunAsync(_workspace.Ws, null, "sql", "validate");
        Assert.Equal(1, r.Exit);
        Assert.Equal("no_project", (string?)r.Json["error"]);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Integration/SqlGen/SqlValidatorTests.cs`:

```csharp
using Dbm.Core.SqlGen;
using Dbm.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dbm.Tests.Integration.SqlGen;

[Trait("Category", "Integration")]
public class SqlValidatorTests
{
    static SqlPlanPayload Plan() => SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());

    static string IdOf(SqlPlanPayload plan, string target) => plan.Tasks.Single(kv => kv.Value.Target == target).Key;

    static Task<ValidationReport> Validate(SamplePair pair, SqlPlanPayload plan, string? only = null) =>
        SqlValidator.ValidateAsync(plan, pair.SourceCs, pair.TargetCs, SampleCatalogs.Target(), only, CancellationToken.None);

    static string Describe(ValidationReport r) =>
        string.Join("\n", r.GlobalErrors.Concat(r.TaskErrors.SelectMany(kv => kv.Value.Select(e => $"{kv.Key}: {e}"))));

    static async Task<object?> Scalar(string cs, string sql)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Generated_sample_plan_validates_ok()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var report = await Validate(pair, plan);
        Assert.True(report.Ok, Describe(report));
        Assert.Equal(plan.Order, report.TaskErrors.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Empty(report.GlobalErrors);
    }

    [Fact]
    public async Task Unknown_source_column_is_reported()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var id = IdOf(plan, "app.Addresses");
        plan.Tasks[id].SourceQuery = plan.Tasks[id].SourceQuery.Replace("s.[CITY]", "s.[CITY_NAME]");
        var report = await Validate(pair, plan);
        Assert.False(report.Ok);
        Assert.Contains(report.TaskErrors[id], e => e == "sourceQuery: Invalid column name 'CITY_NAME'.");
    }

    [Fact]
    public async Task Narrowing_comment_is_a_risk_warning()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var report = await Validate(pair, plan);
        Assert.Contains(report.TaskWarnings[IdOf(plan, "app.Orders")], w => w.StartsWith("Comment: varchar(500) -> nvarchar(200)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_alias_and_non_insertable_target_are_errors()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var id = IdOf(plan, "app.Customers");
        plan.Tasks[id].Columns.Add(new ColumnBinding("FirstName", "DisplayName"));
        plan.Tasks[id].KeyColumns.Add("__k9");
        var report = await Validate(pair, plan, id);
        Assert.Contains("DisplayName: computed column cannot be loaded", report.TaskErrors[id]);
        Assert.Contains("key column '__k9' is not in the source query result", report.TaskErrors[id]);
        Assert.Equal([id], report.TaskErrors.Keys);
    }

    [Fact]
    public async Task Target_statements_are_parse_checked_but_never_executed()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();   // PreSql disables FK_Customers_PrimaryAddress
        plan.PostSql.Add("ALTER TABLE [app].[Customers] NOCHEK CONSTRAINT [FK_Customers_PrimaryAddress];");
        var report = await Validate(pair, plan);
        Assert.Contains("postSql[1]: Incorrect syntax near 'NOCHEK'.", report.GlobalErrors);
        var disabled = await Scalar(pair.TargetCs, "SELECT CAST(is_disabled AS int) FROM sys.foreign_keys WHERE name = 'FK_Customers_PrimaryAddress'");
        Assert.Equal(0, disabled);
    }

    [Fact]
    public async Task Merge_without_terminator_is_reported()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target());
        var id = IdOf(plan, "app.Addresses");
        plan.Tasks[id].MergeSql = "MERGE [app].[Addresses] AS t USING #stg AS s ON t.[AddressId] = s.[AddressId]\n" +
                                  "WHEN NOT MATCHED THEN INSERT ([CustomerId], [Line1], [City], [CountryCode]) VALUES (s.[CustomerId], s.[Line1], s.[City], s.[CountryCode])";
        var report = await Validate(pair, plan, id);
        Assert.Contains("mergeSql: A MERGE statement must be terminated by a semi-colon (;).", report.TaskErrors[id]);
        Assert.DoesNotContain(report.TaskErrors[id], e => e.StartsWith("stagingDdl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_replaces_previous_validator_warnings()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        var plan = Plan();
        var report = await Validate(pair, plan);
        SqlValidator.Apply(plan, report);
        SqlValidator.Apply(plan, report);
        var orders = plan.Tasks[IdOf(plan, "app.Orders")];
        Assert.Single(orders.Warnings, w => w.StartsWith(SqlValidator.WarningPrefix + "Comment:", StringComparison.Ordinal));
        Assert.Contains("target has 1 trigger(s); not fired unless FireTriggers", orders.Warnings);
        Assert.Empty(plan.Errors);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Integration/SqlGen/SqlGenJobTests.cs` (`Prepare` is reused by the command test below):

```csharp
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.SqlGen;

[Trait("Category", "Integration")]
public sealed class SqlGenJobTests
{
    /// <summary>Both sample catalogs saved; the C15 ground-truth mapping stored and approved (when <paramref name="approveMapping"/>).</summary>
    internal static void Prepare(DbmServices s, bool approveMapping = true)
    {
        s.Catalog.Save(Side.Src, SampleCatalogs.Source(), "src-fp");
        s.Catalog.Save(Side.Tgt, SampleCatalogs.Target(), "tgt-fp");
        if (!approveMapping) return;
        var v = s.Artifacts.NextVersion(PhaseName.Mapping);
        s.Artifacts.Add(PhaseName.Mapping, v, Json.Serialize(SampleMappings.Approved()), "human", "approved mapping");
        s.Phases.SetApproved(PhaseName.Mapping, v, null);
    }

    private static JobContext Ctx(DbmServices s, List<string> log) =>
        new() { Services = s, Job = s.Jobs.Get(s.Jobs.Enqueue("sqlgen", PhaseName.Sql))!, Log = log.Add };

    [Fact]
    public async Task Job_generates_and_validates_the_sample_plan()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        Prepare(project.Services);
        var log = new List<string>();

        var result = await new SqlGenJob().RunAsync(Ctx(project.Services, log), CancellationToken.None);

        var plan = Json.FromNode<SqlPlanPayload>(result.DraftPayload!);
        Assert.Equal(6, plan.Tasks.Count);
        Assert.True(plan.ErrorCount() == 0, string.Join("\n", plan.Tasks.SelectMany(t => t.Value.Errors.Select(e => $"{t.Key}: {e}"))));
        Assert.Equal($"6 tasks, 0 errors, {plan.WarningCount()} warnings", result.Summary);
        Assert.Contains(plan.Tasks.Values.Single(t => t.Target == "app.Orders").Warnings,
            w => w.StartsWith("validate: Comment: varchar(500) -> nvarchar(200)", StringComparison.Ordinal));
        Assert.DoesNotContain("Integrated Security", Json.Serialize(plan), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(log, l => l.StartsWith("sqlgen: 6 task(s) generated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Job_requires_an_approved_mapping()
    {
        using var project = TempProject.Create();
        Prepare(project.Services, approveMapping: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SqlGenJob().RunAsync(Ctx(project.Services, new List<string>()), CancellationToken.None));
        Assert.Contains("no approved version", ex.Message);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Integration/SqlGen/SqlCommandsIntegrationTests.cs`:

```csharp
using Dbm.Core.State;
using Dbm.Tests.Support;

namespace Dbm.Tests.Integration.SqlGen;

[Trait("Category", "Integration")]
public sealed class SqlCommandsIntegrationTests
{
    [Fact]
    public async Task Gen_inline_then_validate_one_task()
    {
        await using var pair = await SampleDatabases.CreateAsync(1, seed: false);
        using var project = await SampleProject.CreateAsync(pair);
        SqlGenJobTests.Prepare(project.Services);
        project.Services.Phases.SetStatus(PhaseName.Sql, PhaseStatus.Running);

        var gen = await CliRunner.RunAsync(project.Ws, null, "sql", "gen", "--inline");
        Assert.True(gen.Exit == 0, gen.Out);
        Assert.Equal("awaiting_review", (string?)gen.Json["status"]);
        Assert.StartsWith("6 tasks, 0 errors,", (string?)gen.Json["summary"]);

        var check = await CliRunner.RunAsync(project.Ws, null, "sql", "validate", "--task", "T05");
        Assert.True(check.Exit == 0, check.Out);
        Assert.True((bool)check.Json["ok"]!);
        Assert.NotNull(check.Json["taskErrors"]!["T05"]);
        Assert.DoesNotContain("Integrated Security", check.Out, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run the unit tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.SqlGen.SqlValidatorUnitTests|FullyQualifiedName~Dbm.Tests.Unit.Cli.SqlCommandsTests"`
Expected: build FAILS with `error CS0103: The name 'SqlValidator' does not exist in the current context` and `error CS0246: The type or namespace name 'SqlGenCommand' could not be found` (plus `SqlGenJob` not found in the integration test files).

- [ ] **Step 3: Implement the validator**

`plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlValidator.cs`:

```csharp
using System.Data;
using System.Text.RegularExpressions;
using Dbm.Core.Catalog;
using Dbm.Core.Matching;
using Dbm.Core.Sql;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.SqlGen;

/// <summary>Validates a plan against the live databases without changing either of them.
/// Source: <c>sp_describe_first_result_set</c> (parameterised) for every SourceQuery.
/// Target: catalog checks plus a syntax check of every target-side statement with SET PARSEONLY sent as three
/// separate batches (ON / statement / OFF). Verified on SQL Server 2025 LocalDB: a single batch
/// "SET PARSEONLY ON; stmt; SET PARSEONLY OFF;" EXECUTES stmt (both SETs act at parse time), so never combine them.</summary>
public static class SqlValidator
{
    /// <summary>Prefix of validator warnings stored in TaskPlan.Warnings (lets a re-validation replace them).</summary>
    public const string WarningPrefix = "validate: ";

    static readonly Regex GoLine = new(@"^\s*GO\s*(\d+\s*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed record ResultColumn(string Name, string SystemTypeName, bool IsNullable, int Precision, int Scale);

    public static async Task<ValidationReport> ValidateAsync(SqlPlanPayload plan, string sourceCs, string targetCs, CatalogSnapshot tgt,
        string? onlyTaskId, CancellationToken ct)
    {
        var taskErrors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var taskWarnings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var global = new List<string>();
        if (onlyTaskId is not null && !plan.Tasks.ContainsKey(onlyTaskId))
            return new ValidationReport(false, taskErrors, taskWarnings, [$"unknown task {onlyTaskId}"]);

        var ids = onlyTaskId is not null ? [onlyTaskId]
            : plan.Order.Where(plan.Tasks.ContainsKey).Concat(plan.Tasks.Keys.Where(k => !plan.Order.Contains(k)).OrderBy(k => k, StringComparer.Ordinal)).Distinct().ToList();
        foreach (var id in ids) { taskErrors[id] = new List<string>(); taskWarnings[id] = new List<string>(); }
        var secrets = Redactor.SecretsOf(sourceCs).Concat(Redactor.SecretsOf(targetCs)).ToList();
        string Scrub(string text) => Redactor.Scrub(text, secrets);

        SqlConnection? source = null, target = null;
        try
        {
            try { source = await SqlConnect.OpenAsync(sourceCs, ct); }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
            { global.Add("source connection failed: " + Scrub(ex.Message)); }
            try { target = await SqlConnect.OpenAsync(WithoutPooling(targetCs), ct); }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
            { global.Add("target connection failed: " + Scrub(ex.Message)); }

            foreach (var id in ids)
                await ValidateTaskAsync(plan.Tasks[id], source, target, tgt, taskErrors[id], taskWarnings[id], Scrub, ct);

            if (onlyTaskId is null)
            {
                CheckGlobal("preSql", plan.PreSql);
                CheckGlobal("postSql", plan.PostSql);
                if (target is not null)
                {
                    for (var i = 0; i < plan.PreSql.Count; i++)
                        if (await ParseCheckAsync(target, plan.PreSql[i], ct) is { } e) global.Add($"preSql[{i}]: {Scrub(e)}");
                    for (var i = 0; i < plan.PostSql.Count; i++)
                        if (await ParseCheckAsync(target, plan.PostSql[i], ct) is { } e) global.Add($"postSql[{i}]: {Scrub(e)}");
                }
            }
        }
        finally
        {
            if (source is not null) await source.DisposeAsync();
            if (target is not null) await target.DisposeAsync();
        }
        var ok = global.Count == 0 && taskErrors.Values.All(l => l.Count == 0);
        return new ValidationReport(ok, taskErrors, taskWarnings, global);

        void CheckGlobal(string field, List<string> statements)
        {
            for (var i = 0; i < statements.Count; i++)
                if (GoLine.IsMatch(statements[i])) global.Add($"{field}[{i}]: GO batch separators are not allowed");
        }
    }

    /// <summary>Copies a report into the plan: task Errors are replaced, validator warnings (prefixed) replace the previous
    /// validator warnings; generator warnings stay. <paramref name="full"/> = the report covers the whole plan (sets plan.Errors).</summary>
    public static void Apply(SqlPlanPayload plan, ValidationReport report, bool full = true)
    {
        foreach (var (id, errors) in report.TaskErrors)
            if (plan.Tasks.TryGetValue(id, out var task)) task.Errors = errors.ToList();
        foreach (var (id, warnings) in report.TaskWarnings)
            if (plan.Tasks.TryGetValue(id, out var task))
                task.Warnings = task.Warnings.Where(w => !w.StartsWith(WarningPrefix, StringComparison.Ordinal))
                    .Concat(warnings.Select(w => WarningPrefix + w)).ToList();
        if (full) plan.Errors = report.GlobalErrors.ToList();
    }

    /// <summary>Offline checks of one task (no database needed): mode, required fields, bindings, GO separators.</summary>
    public static List<string> CheckShape(TaskPlan task)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(task.SourceQuery)) errors.Add("sourceQuery is empty");
        if (task.Mode is not ("direct" or "staging_merge")) errors.Add($"mode '{task.Mode}' must be direct or staging_merge");
        if (task.Mode == "staging_merge")
        {
            if (string.IsNullOrWhiteSpace(task.StagingDdl)) errors.Add("staging_merge requires stagingDdl");
            if (string.IsNullOrWhiteSpace(task.MergeSql)) errors.Add("staging_merge requires mergeSql");
        }
        if (task.Columns.Count == 0) errors.Add("no column bindings");
        foreach (var dup in task.Columns.GroupBy(b => b.Target, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"{dup.Key}: bound more than once");
        foreach (var (field, sql) in AllFields(task))
            if (GoLine.IsMatch(sql)) errors.Add($"{field}: GO batch separators are not allowed");
        return errors;
    }

    static async Task ValidateTaskAsync(TaskPlan task, SqlConnection? source, SqlConnection? target, CatalogSnapshot tgt,
        List<string> errors, List<string> warnings, Func<string, string> scrub, CancellationToken ct)
    {
        errors.AddRange(CheckShape(task));
        var table = tgt.FindTable(task.Target);
        if (table is null) errors.Add($"target table {task.Target} not found in the target catalog");

        // Target columns: exist and insertable.
        var targetCols = new Dictionary<string, ColumnInfo>(StringComparer.OrdinalIgnoreCase);
        if (table is not null)
        {
            foreach (var b in task.Columns)
            {
                var col = table.FindColumn(b.Target);
                if (col is null) errors.Add($"{b.Target}: not a column of {task.Target}");
                else if (col.IsComputed) errors.Add($"{b.Target}: computed column cannot be loaded");
                else if (col.IsRowVersion) errors.Add($"{b.Target}: rowversion column cannot be loaded");
                else targetCols[b.Target] = col;
            }
            var identity = table.Columns.FirstOrDefault(c => c.IsIdentity);
            var identityBound = identity is not null && targetCols.ContainsKey(identity.Name);
            if (identityBound && !task.IdentityInsert)
                warnings.Add($"{identity!.Name}: identity column is bound but identityInsert is false; the target will generate new values");
            if (!identityBound && task.IdentityInsert)
                warnings.Add("identityInsert is true but no identity column is bound");
        }

        // Source query shape and types.
        if (source is not null && !string.IsNullOrWhiteSpace(task.SourceQuery))
        {
            var (cols, error) = await DescribeAsync(source, task.SourceQuery, ct);
            if (error is not null) errors.Add("sourceQuery: " + scrub(error));
            else if (cols!.Count == 0) errors.Add("sourceQuery: returns no result set");
            else
            {
                var byName = new Dictionary<string, ResultColumn>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in cols)
                {
                    if (c.Name.Length == 0) { errors.Add("sourceQuery: every result column needs an alias"); continue; }
                    if (!byName.TryAdd(c.Name, c)) errors.Add($"sourceQuery: duplicate result column '{c.Name}'");
                }
                foreach (var key in task.KeyColumns)
                    if (!byName.ContainsKey(key)) errors.Add($"key column '{key}' is not in the source query result");
                foreach (var b in task.Columns)
                {
                    if (!byName.TryGetValue(b.Source, out var rc)) { errors.Add($"{b.Target}: column '{b.Source}' is not in the source query result"); continue; }
                    if (!targetCols.TryGetValue(b.Target, out var col)) continue;
                    var srcType = SqlTypeText.Parse(rc.SystemTypeName, rc.Precision, rc.Scale);
                    var compat = TypeCompat.Check(srcType, ColumnType.From(col));
                    var arrow = $"{SqlTypeText.Format(srcType)} -> {SqlTypeText.Format(col)}";
                    if (compat.Level == CompatLevel.Incompatible) errors.Add($"{b.Target}: {arrow}: {compat.Risk ?? "incompatible types"}");
                    else if (compat.Level == CompatLevel.Risky) warnings.Add($"{b.Target}: {arrow}: {compat.Risk ?? "risky conversion"}");
                    if (rc.IsNullable && !col.IsNullable) warnings.Add($"{b.Target}: source may be NULL but the target column is NOT NULL");
                }
            }
        }

        // Target-side statements: syntax only.
        if (target is not null)
            foreach (var (field, sql) in TargetFields(task))
                if (await ParseCheckAsync(target, sql, ct) is { } e) errors.Add($"{field}: {scrub(e)}");
    }

    /// <summary>Result columns of the first result set, or the compile error message(s).</summary>
    public static async Task<(List<ResultColumn>? Columns, string? Error)> DescribeAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("EXEC sp_describe_first_result_set @tsql = @q;", conn) { CommandTimeout = 60 };
        cmd.Parameters.Add(new SqlParameter("@q", SqlDbType.NVarChar, -1) { Value = sql });
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            int oHidden = r.GetOrdinal("is_hidden"), oName = r.GetOrdinal("name"), oNull = r.GetOrdinal("is_nullable"),
                oType = r.GetOrdinal("system_type_name"), oPrec = r.GetOrdinal("precision"), oScale = r.GetOrdinal("scale");
            var list = new List<ResultColumn>();
            while (await r.ReadAsync(ct))
            {
                if (!r.IsDBNull(oHidden) && r.GetBoolean(oHidden)) continue;
                list.Add(new ResultColumn(
                    r.IsDBNull(oName) ? "" : r.GetString(oName),
                    r.IsDBNull(oType) ? "sql_variant" : r.GetString(oType),
                    !r.IsDBNull(oNull) && r.GetBoolean(oNull),
                    r.IsDBNull(oPrec) ? 0 : Convert.ToInt32(r.GetValue(oPrec)),
                    r.IsDBNull(oScale) ? 0 : Convert.ToInt32(r.GetValue(oScale))));
            }
            return (list, null);
        }
        catch (SqlException ex) { return (null, Messages(ex)); }
    }

    /// <summary>Syntax check on the target: SET PARSEONLY ON / statement / SET PARSEONLY OFF as three batches. Nothing executes.</summary>
    public static async Task<string?> ParseCheckAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;
        await ExecAsync(conn, "SET PARSEONLY ON;", ct);
        try
        {
            await ExecAsync(conn, sql, ct);
            return null;
        }
        catch (SqlException ex) { return Messages(ex); }
        finally { await ExecAsync(conn, "SET PARSEONLY OFF;", CancellationToken.None); }
    }

    static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };   // no parameters => sent as a plain SQL batch
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static string Messages(SqlException ex)
    {
        var parts = ex.Errors.Cast<SqlError>().Select(e => e.Message)
            .Where(m => !m.StartsWith("The batch could not be analyzed", StringComparison.Ordinal)
                     && !m.StartsWith("The metadata could not be determined", StringComparison.Ordinal))
            .Distinct().ToList();
        return parts.Count > 0 ? string.Join(" ", parts) : ex.Message;
    }

    static string WithoutPooling(string cs)
    {
        try { return new SqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString; }
        catch (ArgumentException) { return cs; }
    }

    static IEnumerable<(string Field, string Sql)> TargetFields(TaskPlan task)
    {
        for (var i = 0; i < task.PreSql.Count; i++) yield return ($"preSql[{i}]", task.PreSql[i]);
        if (!string.IsNullOrWhiteSpace(task.StagingDdl)) yield return ("stagingDdl", task.StagingDdl);
        if (!string.IsNullOrWhiteSpace(task.MergeSql)) yield return ("mergeSql", task.MergeSql);
        for (var i = 0; i < task.PostSql.Count; i++) yield return ($"postSql[{i}]", task.PostSql[i]);
    }

    static IEnumerable<(string Field, string Sql)> AllFields(TaskPlan task) =>
        TargetFields(task).Prepend(("sourceQuery", task.SourceQuery));
}
```

Why a non-pooled target connection: if the `SET PARSEONLY OFF;` batch ever failed (e.g. the connection dropped mid-check), a pooled connection could be handed back to the transfer engine still in PARSEONLY mode, where every statement would silently do nothing.

- [ ] **Step 4: Implement the state helpers, the job and the commands**

`plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlPlanSource.cs`:

```csharp
using Dbm.Core.Mapping;
using Dbm.Core.State;

namespace Dbm.Core.SqlGen;

/// <summary>Loads the inputs of the SQL phase from the state store and runs live validation with the saved connections.
/// Shared by SqlGenJob, SqlModule, the `sql` CLI commands and SqlEndpoints.</summary>
public static class SqlPlanSource
{
    /// <summary>The approved Mapping artifact, or null when Mapping is not approved.</summary>
    public static MappingPayload? ApprovedMapping(DbmServices s)
    {
        var version = s.Phases.Get(PhaseName.Mapping).ApprovedVersion;
        if (version is null) return null;
        var row = s.Artifacts.Get(PhaseName.Mapping, version.Value);
        return row is null ? null : Json.Deserialize<MappingPayload>(row.PayloadJson);
    }

    /// <summary>The artifact row of the sql phase's current version, or null.</summary>
    public static ArtifactRow? CurrentRow(DbmServices s)
    {
        var version = s.Phases.Get(PhaseName.Sql).CurrentVersion;
        return version is null ? null : s.Artifacts.Get(PhaseName.Sql, version.Value);
    }

    /// <summary>True when both connections and the target catalog are available.</summary>
    public static bool CanValidate(DbmServices s) =>
        s.Connections.Has(Side.Src) && s.Connections.Has(Side.Tgt) && s.Catalog.Get(Side.Tgt) is not null;

    /// <summary>Validates against the live databases. Throws InvalidOperationException when <see cref="CanValidate"/> is false.</summary>
    public static Task<ValidationReport> ValidateLiveAsync(DbmServices s, SqlPlanPayload plan, string? onlyTaskId, CancellationToken ct)
    {
        var src = s.Connections.GetConnectionString(Side.Src);
        var tgt = s.Connections.GetConnectionString(Side.Tgt);
        var tgtCatalog = s.Catalog.Get(Side.Tgt);
        if (src is null || tgt is null) throw new InvalidOperationException("Both connections must be saved before SQL can be validated.");
        if (tgtCatalog is null) throw new InvalidOperationException("The target catalog is missing; run discovery first.");
        return SqlValidator.ValidateAsync(plan, src, tgt, tgtCatalog, onlyTaskId, ct);
    }
}
```

`plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlGenJob.cs`:

```csharp
using Dbm.Core.Jobs;
using Dbm.Core.State;

namespace Dbm.Core.SqlGen;

/// <summary>Job "sqlgen": approved mapping + catalogs (+ latest Sql artifact as carry-over) → validated plan draft.</summary>
public sealed class SqlGenJob : IJobHandler
{
    public string Kind => "sqlgen";

    public async Task<JobResult> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var s = ctx.Services;
        var mapping = SqlPlanSource.ApprovedMapping(s)
            ?? throw new InvalidOperationException("The mapping phase has no approved version.");
        var src = s.Catalog.Get(Side.Src) ?? throw new InvalidOperationException("The source catalog is missing; run discovery first.");
        var tgt = s.Catalog.Get(Side.Tgt) ?? throw new InvalidOperationException("The target catalog is missing; run discovery first.");
        var latest = s.Artifacts.Latest(PhaseName.Sql);
        var carryOver = latest is null ? null : Json.Deserialize<SqlPlanPayload>(latest.PayloadJson);

        var plan = SqlGenerator.Generate(mapping, src, tgt, carryOver);
        ctx.Log($"sqlgen: {plan.Tasks.Count} task(s) generated{(carryOver is null ? "" : $", carry-over from v{latest!.Version}")}");

        if (SqlPlanSource.CanValidate(s))
        {
            var report = await SqlPlanSource.ValidateLiveAsync(s, plan, null, ct);
            SqlValidator.Apply(plan, report);
            ctx.Log($"sqlgen: validation {(report.Ok ? "ok" : "found errors")}");
        }
        else plan.Warnings.Add("live validation skipped: connections or target catalog missing");

        return new JobResult(Json.ToNode(plan), Summary(plan));
    }

    /// <summary>"6 tasks, 0 errors, 9 warnings".</summary>
    public static string Summary(SqlPlanPayload plan) =>
        $"{plan.Tasks.Count} tasks, {plan.ErrorCount()} errors, {plan.WarningCount()} warnings";
}
```

`plugins/db-migrate/engine/Dbm/Cli/Commands/SqlCommands.cs` (same shape as T3.3's `map auto`):

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Jobs;
using Dbm.Core.Patching;
using Dbm.Core.SqlGen;
using Dbm.Core.State;

namespace Dbm.Cli.Commands;

/// <summary><c>dbm sql gen [--inline]</c>: re-runs the SQL generator while Sql is running or drafting.</summary>
public sealed class SqlGenCommand : ICommand
{
    public string Name => "sql gen";
    public string Help => "Regenerate the SQL plan from the approved mapping (only while Sql is running or drafting) [--inline runs it now]";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        using var services = ctx.OpenProject();
        var status = services.Phases.Get(PhaseName.Sql).Status;
        if (status is not (PhaseStatus.Running or PhaseStatus.Drafting))
            return Output.Fail(ctx, "wrong_phase",
                $"Sql is {EnumText.ToText(status)}; `sql gen` only runs while Sql is running or drafting.");

        services.Db.InTransaction(() =>
        {
            // WorkflowEngine.OnJobDone only accepts a job result while the phase is running, so a drafting phase goes back to running.
            if (status == PhaseStatus.Drafting) services.Phases.SetStatus(PhaseName.Sql, PhaseStatus.Running);
            if (!services.Jobs.Active().Any(j => j.Phase == PhaseName.Sql)) services.Workflow.RetryJob(PhaseName.Sql);
        });
        if (!args.Flag("inline")) return Output.Ok(ctx, new { ok = true, queued = "sqlgen" });

        var ran = await new JobRunner(services).RunPendingAsync(CancellationToken.None);
        var job = services.Jobs.LatestFor(PhaseName.Sql);
        if (job is { Status: JobStatus.Failed })
            return Output.Fail(ctx, "job_failed", job.Error ?? "sqlgen failed");
        var phase = services.Phases.Get(PhaseName.Sql);
        return Output.Ok(ctx, new
        {
            ok = true,
            ran,
            status = EnumText.ToText(phase.Status),
            version = phase.CurrentVersion,
            summary = services.Artifacts.Latest(PhaseName.Sql)?.Summary
        });
    }
}

/// <summary><c>dbm sql validate [--task T04] [--patch file]</c>: validates the current sql version (optionally with a patch applied
/// in memory) against the live databases and prints the ValidationReport; exit 1 when it is not ok. Nothing is stored.</summary>
public sealed class SqlValidateCommand : ICommand
{
    public string Name => "sql validate";
    public string Help => "Validate the current SQL plan live [--task <id>] [--patch <file> checks a patch first]; exit 1 when not ok";

    public async Task<int> RunAsync(Args args, CliContext ctx)
    {
        using var services = ctx.OpenProject();
        var row = SqlPlanSource.CurrentRow(services) ?? throw new CliFailure("no_version", "The sql phase has no version yet.");

        var node = JsonNode.Parse(row.PayloadJson)!;
        var patchPath = args.Opt("patch");
        if (patchPath is not null)
        {
            if (!File.Exists(patchPath)) throw new CliFailure("not_found", $"Patch file not found: {patchPath}");
            try
            {
                var patch = Patch.Parse(File.ReadAllText(patchPath));
                if (patch.BaseVersion != row.Version)
                    throw new CliFailure("stale_patch", $"The patch is based on v{patch.BaseVersion} but the current version is v{row.Version}.");
                node = JsonPatch.Apply(node, patch.Ops);
            }
            catch (PatchException ex)
            {
                throw new CliFailure("invalid_patch", ex.Message);
            }
        }

        SqlPlanPayload plan;
        try
        {
            plan = Json.FromNode<SqlPlanPayload>(node);
        }
        catch (JsonException ex)
        {
            throw new CliFailure("bad_payload", ex.Message);
        }
        if (!SqlPlanSource.CanValidate(services))
            throw new CliFailure("not_ready", "Both connections and the target catalog are needed to validate.");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var report = await SqlPlanSource.ValidateLiveAsync(services, plan, args.Opt("task"), cts.Token);
        return Output.Write(ctx, new
        {
            ok = report.Ok,
            version = row.Version,
            patched = patchPath is not null,
            taskErrors = report.TaskErrors,
            taskWarnings = report.TaskWarnings,
            globalErrors = report.GlobalErrors,
        }, report.Ok ? 0 : 1);
    }
}
```

`sql gen --inline` runs every queued job through `JobRunner.RunPendingAsync`, which calls `WorkflowEngine.OnJobDone`, so the draft is stored exactly as the server would store it. Use it offline or in tests; with the server running, plain `sql gen` queues the job for the server's loop.

- [ ] **Step 5: Register the job and the commands**

`plugins/db-migrate/engine/Dbm/Core/JobRegistry.cs` — below the `// milestone registrations below` marker, directly after the line `        yield return new Dbm.Core.Matching.AutomapJob();   // T3.3`, add:

```csharp
        yield return new Dbm.Core.SqlGen.SqlGenJob();    // T4.3
```

`plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs` — in the collection returned by `All()`, directly after the line `        new MapAutoCommand(),    // T3.3`, add (the file already has `using Dbm.Cli.Commands;`):

```csharp
        new SqlGenCommand(),     // T4.3
        new SqlValidateCommand(), // T4.3
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.SqlGen.SqlValidatorUnitTests|FullyQualifiedName~Dbm.Tests.Unit.Cli.SqlCommandsTests"`
Expected: `Passed!  - Failed: 0, Passed: 9`.

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category=Integration&FullyQualifiedName~Dbm.Tests.Integration.SqlGen"`
Expected: `Passed!  - Failed: 0, Passed: 10` (7 validator + 2 job + 1 command; each test creates and drops its own LegacyShop/ShopV2 pair on `DBM_TEST_SQL`).

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"` — Expected: no failures (the registries now include `sqlgen` and the two commands).

Run: `dotnet run --project plugins/db-migrate/engine/Dbm -- help`
Expected: the output lists `sql gen` and `sql validate` with their help lines.

- [ ] **Step 7: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlValidator.cs plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlPlanSource.cs plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlGenJob.cs plugins/db-migrate/engine/Dbm/Cli/Commands/SqlCommands.cs plugins/db-migrate/engine/Dbm/Core/JobRegistry.cs plugins/db-migrate/engine/Dbm/Cli/CommandRegistry.cs plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/SqlValidatorUnitTests.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Cli/SqlCommandsTests.cs plugins/db-migrate/engine/Dbm.Tests/Integration/SqlGen
git commit -F - <<'EOF'
feat(sqlgen): validate plans live, add sqlgen job and sql gen/validate commands

SET PARSEONLY is sent as three separate batches: in one batch the
statement between ON and OFF is executed (verified on LocalDB).

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 4.4: SQL module, sql-engineer agent, script pack

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/Core/SqlGen/TaskListing.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlModule.cs`
- Create: `plugins/db-migrate/engine/Dbm/Core/SqlGen/ScriptPack.cs`
- Create: `plugins/db-migrate/engine/Dbm/Web/Endpoints/SqlExports.cs`
- Create: `plugins/db-migrate/agents/sql-engineer.md`
- Modify: `plugins/db-migrate/engine/Dbm/Core/ModuleRegistry.cs` (T1.6) — one line
- Modify: `plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs` (T1.7) — one line
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/SqlModuleTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/ScriptPackTests.cs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Web/SqlExportsTests.cs`

**Interfaces:**
- Consumes: `IPhaseModule`, `ModuleContext`, `PacketMode`, `PayloadCheck` (T1.5); `DbmServices`, `ArtifactRow`, `FeedbackRow`, `ConnectionRepo.GetMeta`, `CatalogRepo.Get`, `ProjectRepo.Get` (C2/C6); `ServerMeta` (C3); `WebState`, `IEndpointRouteBuilder` (T1.7); `ExportEndpoints.Register(string what, Builder builder)` with `delegate Task<ExportFile> Builder(WebState, IWebAssets, CancellationToken)`, `ExportFile(FileName, ContentType, Content)`, `ExportException`, `IWebAssets`, `WebExport.BuildHtml(assets, view, title, payload)` (T2.8); `Clock.NowText()` (C1); `SqlPlanSource`, `SqlValidator.CheckShape/Apply`, `SqlGenerator.CountSql` (T4.2/T4.3). Tests: `TestWorkspace.OpenServices()`, `WebTestServer.StartAsync(ws, factory)` (M1), `WorkflowEngine.HumanEdit` (T1.5), `SampleCatalogs`, `SampleMappings` (T3.2).
- Produces:
  - `public sealed record ListingLine(int No, string Section, string Text);` and `public static class TaskListing { List<ListingLine> Build(TaskPlan task); string Render(TaskPlan task, int? highlight = null); }` — the canonical numbering behind `sql:<taskId>:<line>` anchors: sections `pre` (each statement), `source`, `staging`, `merge`, `post` (each statement) in that order, empty ones skipped, `\r\n` → `\n`, 1-based and continuous. `views/sql.js` (T4.5) implements the same rules in `DBM.sqlView.listing`.
  - `public sealed class SqlModule : IPhaseModule` (`Phase = Sql`, `Agent = "sql-engineer"`, `JobKind = "sqlgen"`, parameterless constructor) plus `public static (string? TaskId, int? Line) ParseAnchor(string? anchor)` and `public const string SkippedWarning`.
  - `public static class ScriptPack { byte[] BuildZip(SqlPlanPayload plan, string projectName); List<(string Name, string Content)> BuildFiles(SqlPlanPayload plan, string projectName); }`
  - `public static class SqlExports { void Map(IEndpointRouteBuilder app, WebState state); ExportFile Pack(DbmServices services); ExportFile Html(DbmServices services, IWebAssets assets); }` — `Map` registers two T2.8 export kinds for the sql phase's **current** version: `sqlpack` (zip `<project>-sql-v<n>.zip`) and `sql` (standalone HTML `sql-v<n>.html` of the SQL screen; it needs `views/sql.js` from T4.5 and answers 404 until then). Served by T2.8 as `GET /api/export/{what}` (download) and `POST /api/export/{what}` (`{ok, file, path, download}`); both save a copy under `.dbmigrate/exports/`. No version yet → 404 `{"error":"export_unavailable","message":"No SQL plan yet."}`.

**Module behaviour:**
- `NeedsAgent(draft)`: any plan error, any task with errors, or any `staging_merge` task that is not yet `Custom`.
- `BuildPacket` data: `legend`, `servers` (src/tgt version, major, edition, compat level, collation — from `ConnectionRepo.GetMeta`, never the connection string), `order`, `pre`, `post`, `planErrors`, `work` (tasks needing work in full: SQL fields, errors, warnings, bindings, keys, JSON-pointer `paths`, the task's approved `tableMap` without candidates, `targetColumns` as `"Name type NULL|NOT NULL [IDENTITY|COMPUTED|ROWVERSION] [DEFAULT x]"`), `others` (id, target, mode, cols, keys, warns count, custom); rework adds `context` keyed by feedback id: `task:<id>` → the task in full; `sql:<id>:<line>` → the task in full + `line`, `section`, `listing` (`TaskListing.Render` with `>>` on the line); other/null anchors → `{anchor:"general"}`.
- `Validate(ctx, payload)`: every task whose SQL fields (SourceQuery, StagingDdl, MergeSql, PreSql, PostSql, Mode, KeyColumns, Columns, IdentityInsert, ChunkSize) differ from `ctx.Current` gets `Custom = true`; `CountSql` is recomputed; live validation via `SqlPlanSource` when connections + target catalog exist (errors reject the patch, results are stored in the task Errors/Warnings), else `CheckShape` only plus warning `SkippedWarning`. **The payload node is normalised in place**: T1.5's `WorkflowEngine.Apply` passes the patched node to `Validate` and then stores that same node (`StoreArtifact(phase, updated, …)`), so the flags reach the new version — `Human_edit_stores_the_custom_flag_set_by_validate` pins this.
- `ApprovalBlockers`: plan errors, every task with errors (`"T05 (app.Orders): 1 validation error(s)"`), and `"order must list every task id exactly once"` when `Order` is not a permutation of `Tasks` keys.
- `Summarize`: `"6 tasks, 1 custom, 9 warnings"` (+ `", N errors"` when N > 0).

- [ ] **Step 1: Write the failing tests**

`plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/SqlModuleTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.Mapping;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Core.Workflow;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.SqlGen;

public sealed class SqlModuleTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly DbmServices _services;
    private readonly SqlModule _module = new();

    public SqlModuleTests()
    {
        _services = _workspace.OpenServices();
        _services.Catalog.Save(Side.Src, SampleCatalogs.Source(), "src-fp");
        _services.Catalog.Save(Side.Tgt, SampleCatalogs.Target(), "tgt-fp");
        var mv = _services.Artifacts.NextVersion(PhaseName.Mapping);
        _services.Artifacts.Add(PhaseName.Mapping, mv, Json.Serialize(SampleMappings.Approved()), "human", "approved mapping");
        _services.Phases.SetApproved(PhaseName.Mapping, mv, null);
    }

    public void Dispose() => _workspace.Dispose();

    private static SqlPlanPayload Plan(MappingPayload? mapping = null) =>
        SqlGenerator.Generate(mapping ?? SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());

    private ModuleContext Ctx(SqlPlanPayload plan, params FeedbackRow[] feedback)
    {
        var v = _services.Artifacts.NextVersion(PhaseName.Sql);
        var row = _services.Artifacts.Add(PhaseName.Sql, v, Json.Serialize(plan), "script", "draft");
        _services.Phases.SetCurrentVersion(PhaseName.Sql, v);
        return new ModuleContext { Services = _services, Current = row, OpenFeedback = feedback };
    }

    private static FeedbackRow Fb(long id, string? anchor, string text) =>
        new(id, PhaseName.Sql, 0, anchor, text, FeedbackStatus.Open, null, null, DateTimeOffset.UtcNow);

    [Fact]
    public void Identity_matches_the_contract()
    {
        Assert.Equal(PhaseName.Sql, _module.Phase);
        Assert.Equal("sql-engineer", _module.Agent);
        Assert.Equal("sqlgen", _module.JobKind);
        Assert.IsType<SqlModule>(_services.Modules[PhaseName.Sql]);
    }

    [Fact]
    public void NeedsAgent_only_for_errors_or_unreviewed_staging_tasks()
    {
        Assert.False(_module.NeedsAgent(Json.ToNode(Plan())));

        var withError = Plan();
        withError.Tasks["T05"].Errors.Add("sourceQuery: Invalid column name 'X'.");
        Assert.True(_module.NeedsAgent(Json.ToNode(withError)));

        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";
        var lookup = Plan(mapping);
        Assert.True(_module.NeedsAgent(Json.ToNode(lookup)));
        lookup.Tasks["T04"].Custom = true;
        Assert.False(_module.NeedsAgent(Json.ToNode(lookup)));
    }

    [Fact]
    public void Draft_packet_details_tasks_needing_work_and_summarises_the_rest()
    {
        var plan = Plan();
        plan.Tasks["T05"].Errors.Add("sourceQuery: Invalid column name 'X'.");
        var data = _module.BuildPacket(Ctx(plan), PacketMode.Draft).AsObject();

        var item = Assert.Single(data["work"]!.AsArray())!.AsObject();
        Assert.Equal("T05", (string?)item["id"]);
        Assert.Equal(plan.Tasks["T05"].SourceQuery, (string?)item["sourceQuery"]);
        Assert.Equal("merge", (string?)item["tableMap"]!["kind"]);
        Assert.Contains("Comment nvarchar(200) NULL", item["targetColumns"]!.AsArray().Select(n => (string?)n));
        Assert.Contains("OrderId int NOT NULL IDENTITY", item["targetColumns"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("/tasks/T05/sourceQuery", (string?)item["paths"]!["sourceQuery"]);
        Assert.Equal("sourceQuery: Invalid column name 'X'.", (string?)item["errors"]![0]);

        var others = data["others"]!.AsArray();
        Assert.Equal(5, others.Count);
        Assert.All(others, o => Assert.Null(o!["sourceQuery"]));
        Assert.Equal(["T01", "T02", "T03", "T04", "T06"], others.Select(o => (string?)o!["id"]));
        Assert.NotNull(data["legend"]!["sourceQuery"]);
        Assert.Equal("ALTER TABLE [app].[Customers] NOCHECK CONSTRAINT [FK_Customers_PrimaryAddress];", (string?)data["pre"]![0]);
        Assert.Null(data["context"]);
    }

    [Fact]
    public void Draft_packet_includes_lookup_tasks_in_full()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";
        var data = _module.BuildPacket(Ctx(Plan(mapping)), PacketMode.Draft).AsObject();
        var item = Assert.Single(data["work"]!.AsArray())!.AsObject();
        Assert.Equal("T04", (string?)item["id"]);
        Assert.StartsWith("CREATE TABLE #stg (", (string?)item["stagingDdl"]);
        Assert.EndsWith("FROM #stg;", (string?)item["mergeSql"]);
    }

    [Fact]
    public void Rework_packet_gives_context_per_feedback_anchor()
    {
        var plan = Plan();   // T04 = app.Addresses; listing line 9 = "FROM [dbo].[ADDR] AS s"
        var ctx = Ctx(plan, Fb(12, "sql:T04:9", "Skip addresses without a city"), Fb(13, "task:T02", "Load customers last"), Fb(14, null, "Looks good overall"));
        var data = _module.BuildPacket(ctx, PacketMode.Rework).AsObject();
        var context = data["context"]!.AsObject();

        Assert.Equal(9, (int?)context["12"]!["line"]);
        Assert.Equal("source", (string?)context["12"]!["section"]);
        Assert.Contains("   9 source  >> FROM [dbo].[ADDR] AS s", (string?)context["12"]!["listing"]);
        Assert.Contains("   8 source  |      s.[ADDR_ID] AS [__k0]", (string?)context["12"]!["listing"]);
        Assert.Equal("T04", (string?)context["12"]!["task"]!["id"]);
        Assert.Equal("T02", (string?)context["13"]!["task"]!["id"]);
        Assert.Equal("general", (string?)context["14"]!["anchor"]);
        Assert.Empty(data["work"]!.AsArray());
        Assert.Equal(["T01", "T03", "T05", "T06"], data["others"]!.AsArray().Select(o => (string?)o!["id"]));
    }

    [Fact]
    public void Validate_marks_edited_tasks_custom_and_recomputes_the_count_query()
    {
        var plan = Plan();
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        var edited = plan.Tasks["T04"].SourceQuery + "\nWHERE s.[CITY] <> ''";
        payload["tasks"]!["T04"]!["sourceQuery"] = edited;

        var check = _module.Validate(ctx, payload);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Contains(SqlModule.SkippedWarning, check.Warnings);
        Assert.True((bool)payload["tasks"]!["T04"]!["custom"]!);
        Assert.False((bool)payload["tasks"]!["T05"]!["custom"]!);
        Assert.Equal(SqlGenerator.CountSql(edited), (string?)payload["tasks"]!["T04"]!["countSql"]);
    }

    [Fact]
    public void Validate_rejects_shape_errors_when_offline()
    {
        var plan = Plan();
        var ctx = Ctx(plan);
        var payload = Json.ToNode(plan);
        payload["tasks"]!["T04"]!["mode"] = "bulk";
        payload["tasks"]!["T05"]!["preSql"] = new JsonArray("SELECT 1\nGO");
        var check = _module.Validate(ctx, payload);
        Assert.False(check.Ok);
        Assert.Contains("T04: mode 'bulk' must be direct or staging_merge", check.Errors);
        Assert.Contains("T05: preSql[0]: GO batch separators are not allowed", check.Errors);
    }

    [Fact]
    public void Human_edit_stores_the_custom_flag_set_by_validate()
    {
        var plan = Plan();
        Ctx(plan);
        _services.Phases.SetStatus(PhaseName.Sql, PhaseStatus.AwaitingReview);
        var current = _services.Phases.Get(PhaseName.Sql).CurrentVersion!.Value;
        var edited = plan.Tasks["T04"].SourceQuery + "\nWHERE s.[CITY] <> ''";

        var result = _services.Workflow.HumanEdit(new Dbm.Core.Patching.Patch("sql", current,
            [new Dbm.Core.Patching.PatchOp("replace", "/tasks/T04/sourceQuery", JsonValue.Create(edited))], []));

        Assert.True(result.Ok, string.Join("; ", result.Errors));
        var stored = Json.Deserialize<SqlPlanPayload>(_services.Artifacts.Get(PhaseName.Sql, result.Version!.Value)!.PayloadJson);
        Assert.True(stored.Tasks["T04"].Custom);
        Assert.Equal(SqlGenerator.CountSql(edited), stored.Tasks["T04"].CountSql);
    }

    [Fact]
    public void Approval_is_blocked_by_errors_and_a_broken_order()
    {
        var plan = Plan();
        var ctx = Ctx(plan);
        Assert.Empty(_module.ApprovalBlockers(ctx, Json.ToNode(plan)));

        plan.Tasks["T05"].Errors.Add("sourceQuery: Invalid column name 'X'.");
        plan.Order.RemoveAt(0);
        var blockers = _module.ApprovalBlockers(ctx, Json.ToNode(plan));
        Assert.Contains("T05 (app.Orders): 1 validation error(s)", blockers);
        Assert.Contains("order must list every task id exactly once", blockers);
    }

    [Fact]
    public void Summary_counts_tasks_custom_and_warnings()
    {
        var plan = Plan();
        plan.Tasks["T04"].Custom = true;
        Assert.Equal($"6 tasks, 1 custom, {plan.WarningCount()} warnings", _module.Summarize(Json.ToNode(plan)));
        plan.Tasks["T04"].Errors.Add("x");
        Assert.EndsWith(", 1 errors", _module.Summarize(Json.ToNode(plan)));
    }

    [Theory]
    [InlineData("task:T04", "T04", null)]
    [InlineData("sql:T04:12", "T04", 12)]
    [InlineData("sql:T04:x", null, null)]
    [InlineData("tablemap:app.Orders", null, null)]
    [InlineData(null, null, null)]
    public void ParseAnchor_understands_task_and_line_anchors(string? anchor, string? task, int? line)
    {
        var (t, l) = SqlModule.ParseAnchor(anchor);
        Assert.Equal(task, t);
        Assert.Equal(line, l);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/ScriptPackTests.cs`:

```csharp
using System.IO.Compression;
using Dbm.Core.SqlGen;
using Dbm.Tests.Support;
using Xunit;

namespace Dbm.Tests.Unit.SqlGen;

public class ScriptPackTests
{
    static SqlPlanPayload Plan() => SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());

    static List<(string Name, string Content)> Unzip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.Select(e =>
        {
            using var r = new StreamReader(e.Open());
            return (e.FullName, r.ReadToEnd());
        }).ToList();
    }

    [Fact]
    public void Zip_has_pre_one_file_per_task_post_and_readme_in_order()
    {
        var entries = Unzip(ScriptPack.BuildZip(Plan(), "LegacyShop to ShopV2"));
        Assert.Equal(
            ["00_pre.sql", "01_app_AuditEvents.sql", "02_app_Customers.sql", "03_app_Products.sql", "04_app_Addresses.sql",
             "05_app_Orders.sql", "06_app_OrderLines.sql", "99_post.sql", "README.md"],
            entries.Select(e => e.Name));
    }

    [Fact]
    public void Task_file_has_header_source_query_and_count_query()
    {
        var plan = Plan();
        var files = Unzip(ScriptPack.BuildZip(plan, "demo")).ToDictionary(e => e.Name, e => e.Content);
        var orders = files["05_app_Orders.sql"];
        Assert.Contains("-- demo: task T05  app.Orders\n", orders);
        Assert.Contains("-- Mode:            direct\n", orders);
        Assert.Contains("-- Keys:            __k0\n", orders);
        Assert.Contains("-- Depends on:      T02, T04\n", orders);
        Assert.Contains("-- Identity insert: yes\n", orders);
        Assert.Contains("--   - target has 1 trigger(s); not fired unless FireTriggers\n", orders);
        Assert.Contains("-- ---- Source query ----\n-- runs on SOURCE\n", orders);
        Assert.Contains(plan.Tasks["T05"].SourceQuery + "\nGO\n", orders);
        Assert.Contains(plan.Tasks["T05"].CountSql + ";\nGO\n", orders);
        Assert.Contains("-- Keys:            (none - the table loads in a single transaction)\n", files["01_app_AuditEvents.sql"]);
    }

    [Fact]
    public void Global_files_hold_the_cycle_statements()
    {
        var files = Unzip(ScriptPack.BuildZip(Plan(), "demo")).ToDictionary(e => e.Name, e => e.Content);
        Assert.Contains("ALTER TABLE [app].[Customers] NOCHECK CONSTRAINT [FK_Customers_PrimaryAddress];\nGO\n", files["00_pre.sql"]);
        Assert.Contains("ALTER TABLE [app].[Customers] WITH CHECK CHECK CONSTRAINT [FK_Customers_PrimaryAddress];\nGO\n", files["99_post.sql"]);
        Assert.Contains("-- runs on TARGET", files["00_pre.sql"]);
    }

    [Fact]
    public void Empty_global_scripts_say_so()
    {
        var plan = Plan();
        plan.PreSql.Clear();
        var files = Unzip(ScriptPack.BuildZip(plan, "demo")).ToDictionary(e => e.Name, e => e.Content);
        Assert.Contains("-- (no global pre-load statements)", files["00_pre.sql"]);
    }

    [Fact]
    public void Staging_task_file_contains_staging_and_merge()
    {
        var mapping = SampleMappings.Approved();
        mapping.Tables["app.Addresses"].Kind = "lookup";
        var plan = SqlGenerator.Generate(mapping, SampleCatalogs.Source(), SampleCatalogs.Target());
        var file = Unzip(ScriptPack.BuildZip(plan, "demo")).Single(e => e.Name == "04_app_Addresses.sql").Content;
        Assert.Contains("-- Mode:            staging_merge\n", file);
        Assert.Contains(plan.Tasks["T04"].StagingDdl + "\nGO\n", file);
        Assert.Contains(plan.Tasks["T04"].MergeSql + "\nGO\n", file);
    }

    [Fact]
    public void Readme_explains_streaming_and_lists_files()
    {
        var readme = Unzip(ScriptPack.BuildZip(Plan(), "demo")).Single(e => e.Name == "README.md").Content;
        Assert.StartsWith("# demo - migration script pack", readme);
        Assert.Contains("no linked server", readme);
        Assert.Contains("SqlBulkCopy", readme);
        Assert.Contains("| `05_app_Orders.sql` | T05 | app.Orders | direct | T02, T04 |", readme);
    }
}
```

`plugins/db-migrate/engine/Dbm.Tests/Unit/Web/SqlExportsTests.cs` (`WithPlan` is reused by T4.5's endpoint tests):

```csharp
using System.IO.Compression;
using System.Net;
using Dbm.Core;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Dbm.Tests.Support;
using Dbm.Web.Endpoints;

namespace Dbm.Tests.Unit.Web;

public sealed class SqlExportsTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    /// <summary>Stores the generated sample plan as sql v0 (current version).</summary>
    internal static DbmServices WithPlan(TestWorkspace workspace)
    {
        var s = workspace.OpenServices();
        var plan = SqlGenerator.Generate(SampleMappings.Approved(), SampleCatalogs.Source(), SampleCatalogs.Target());
        var v = s.Artifacts.NextVersion(PhaseName.Sql);
        s.Artifacts.Add(PhaseName.Sql, v, Json.Serialize(plan), "script", "draft");
        s.Phases.SetCurrentVersion(PhaseName.Sql, v);
        return s;
    }

    [Fact]
    public void Pack_is_named_after_the_project_and_version()
    {
        var file = SqlExports.Pack(WithPlan(_workspace));
        Assert.Equal("test-sql-v0.zip", file.FileName);
        Assert.Equal("application/zip", file.ContentType);
        using var zip = new ZipArchive(new MemoryStream(file.Content), ZipArchiveMode.Read);
        Assert.Contains(zip.Entries, e => e.FullName == "05_app_Orders.sql");
    }

    [Fact]
    public void Pack_without_a_plan_is_unavailable()
    {
        var ex = Assert.Throws<ExportException>(() => SqlExports.Pack(_workspace.OpenServices()));
        Assert.Equal("No SQL plan yet.", ex.Message);
    }

    [Fact]
    public async Task Sqlpack_is_served_by_the_export_endpoint()
    {
        WithPlan(_workspace);
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));

        using var response = await server.Client.GetAsync("/api/export/sqlpack");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.Equal(9, zip.Entries.Count);
        Assert.True(File.Exists(Path.Combine(_workspace.Ws.ExportsDir, "test-sql-v0.zip")));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.SqlGen.SqlModuleTests|FullyQualifiedName~Dbm.Tests.Unit.SqlGen.ScriptPackTests|FullyQualifiedName~Dbm.Tests.Unit.Web.SqlExportsTests"`
Expected: build FAILS with `error CS0246: The type or namespace name 'SqlModule' could not be found` and `error CS0103: The name 'ScriptPack' does not exist in the current context` / `'SqlExports' does not exist`.

- [ ] **Step 3: Implement the listing and the module**

`plugins/db-migrate/engine/Dbm/Core/SqlGen/TaskListing.cs`:

```csharp
namespace Dbm.Core.SqlGen;

public sealed record ListingLine(int No, string Section, string Text);

/// <summary>The canonical line numbering of one task's SQL, shared by line anchors (<c>sql:&lt;taskId&gt;:&lt;line&gt;</c>),
/// work packets and the UI (views/sql.js <c>DBM.sqlView.listing</c> implements the same rules).
/// Sections in execution order: pre (each statement), source, staging, merge, post (each statement);
/// empty sections are skipped; "\r\n" is normalised to "\n"; numbering is 1-based and continuous across sections.</summary>
public static class TaskListing
{
    public static List<ListingLine> Build(TaskPlan task)
    {
        var lines = new List<ListingLine>();
        var no = 0;
        foreach (var s in task.PreSql) Add("pre", s);
        Add("source", task.SourceQuery);
        Add("staging", task.StagingDdl);
        Add("merge", task.MergeSql);
        foreach (var s in task.PostSql) Add("post", s);
        return lines;

        void Add(string section, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var line in text.Replace("\r\n", "\n").Split('\n')) lines.Add(new ListingLine(++no, section, line));
        }
    }

    /// <summary>"  12 source  >> FROM [dbo].[ADDR] AS s" — the highlighted line gets "&gt;&gt;", others "| ".</summary>
    public static string Render(TaskPlan task, int? highlight = null) =>
        string.Join("\n", Build(task).Select(l => $"{l.No,4} {l.Section,-7} {(l.No == highlight ? ">>" : "| ")} {l.Text}"));
}
```

`plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlModule.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbm.Core.Catalog;
using Dbm.Core.Mapping;
using Dbm.Core.Sql;
using Dbm.Core.State;
using Dbm.Core.Workflow;

namespace Dbm.Core.SqlGen;

/// <summary>Phase module for the SQL review loop (agent "sql-engineer", job "sqlgen").</summary>
public sealed class SqlModule : IPhaseModule
{
    static readonly TimeSpan LiveTimeout = TimeSpan.FromMinutes(3);
    public const string SkippedWarning = "live validation skipped: connections or target catalog missing";

    public PhaseName Phase => PhaseName.Sql;
    public string Agent => "sql-engineer";
    public string JobKind => "sqlgen";

    public bool NeedsAgent(JsonNode draft)
    {
        var plan = Json.FromNode<SqlPlanPayload>(draft);
        return plan.Errors.Count > 0 || plan.Tasks.Values.Any(NeedsWork);
    }

    static bool NeedsWork(TaskPlan t) => t.Errors.Count > 0 || (t.Mode == "staging_merge" && !t.Custom);

    public JsonNode BuildPacket(ModuleContext ctx, PacketMode mode)
    {
        var plan = Json.Deserialize<SqlPlanPayload>(ctx.Current.PayloadJson);
        var mapping = SqlPlanSource.ApprovedMapping(ctx.Services);
        var tgt = ctx.Services.Catalog.Get(Side.Tgt);
        var data = new JsonObject
        {
            ["legend"] = Legend(),
            ["servers"] = new JsonObject
            {
                ["src"] = ServerNode(ctx.Services.Connections.GetMeta(Side.Src)),
                ["tgt"] = ServerNode(ctx.Services.Connections.GetMeta(Side.Tgt)),
            },
            ["order"] = Array(plan.Order),
            ["pre"] = Array(plan.PreSql),
            ["post"] = Array(plan.PostSql),
            ["planErrors"] = Array(plan.Errors),
        };

        var detailed = new HashSet<string>(StringComparer.Ordinal);
        if (mode == PacketMode.Rework)
        {
            var context = new JsonObject();
            foreach (var fb in ctx.OpenFeedback)
            {
                var (taskId, line) = ParseAnchor(fb.Anchor);
                if (taskId is null || !plan.Tasks.TryGetValue(taskId, out var task))
                {
                    context[fb.Id.ToString()] = new JsonObject { ["anchor"] = fb.Anchor ?? "general", ["scope"] = "whole plan: see order/pre/post and the task summaries" };
                    continue;
                }
                var item = new JsonObject { ["anchor"] = fb.Anchor, ["task"] = Detail(taskId, task, mapping, tgt) };
                if (line is not null)
                {
                    var listed = TaskListing.Build(task).FirstOrDefault(l => l.No == line);
                    item["line"] = line;
                    item["section"] = listed?.Section ?? "(line no longer exists)";
                    item["listing"] = TaskListing.Render(task, line);
                }
                context[fb.Id.ToString()] = item;
                detailed.Add(taskId);
            }
            data["context"] = context;
        }

        var work = new JsonArray();
        foreach (var id in plan.Order)
            if (plan.Tasks.TryGetValue(id, out var t) && !detailed.Contains(id) && (NeedsWork(t) || (mode == PacketMode.Rework && t.Errors.Count > 0)))
            {
                work.Add(Detail(id, t, mapping, tgt));
                detailed.Add(id);
            }
        data["work"] = work;

        var others = new JsonArray();
        foreach (var id in plan.Order)
            if (plan.Tasks.TryGetValue(id, out var t) && !detailed.Contains(id))
                others.Add(new JsonObject
                {
                    ["id"] = id,
                    ["target"] = t.Target,
                    ["mode"] = t.Mode,
                    ["cols"] = Array(t.Columns.Select(ColumnText)),
                    ["keys"] = Array(t.KeyColumns),
                    ["warns"] = t.Warnings.Count,
                    ["custom"] = t.Custom,
                });
        data["others"] = others;
        return data;
    }

    public PayloadCheck Validate(ModuleContext ctx, JsonNode payload)
    {
        SqlPlanPayload plan;
        try { plan = Json.FromNode<SqlPlanPayload>(payload); }
        catch (JsonException ex) { return new PayloadCheck([$"payload is not a SQL plan: {ex.Message}"], []); }
        SqlPlanPayload? basePlan = null;
        try { basePlan = Json.Deserialize<SqlPlanPayload>(ctx.Current.PayloadJson); } catch (JsonException) { }

        foreach (var (id, task) in plan.Tasks)
        {
            var before = basePlan?.Tasks.GetValueOrDefault(id);
            if (before is null || SqlChanged(task, before)) task.Custom = true;
            task.CountSql = SqlGenerator.CountSql(task.SourceQuery);
        }

        var errors = new List<string>();
        var warnings = new List<string>();
        if (SqlPlanSource.CanValidate(ctx.Services))
        {
            using var cts = new CancellationTokenSource(LiveTimeout);
            var report = SqlPlanSource.ValidateLiveAsync(ctx.Services, plan, null, cts.Token).GetAwaiter().GetResult();
            SqlValidator.Apply(plan, report);
            errors.AddRange(report.GlobalErrors);
            foreach (var id in plan.Order.Concat(plan.Tasks.Keys).Distinct())
                if (report.TaskErrors.TryGetValue(id, out var list)) errors.AddRange(list.Select(e => $"{id}: {e}"));
        }
        else
        {
            warnings.Add(SkippedWarning);
            foreach (var (id, task) in plan.Tasks) errors.AddRange(SqlValidator.CheckShape(task).Select(e => $"{id}: {e}"));
        }

        ReplaceContent(payload, plan);
        return new PayloadCheck(errors, warnings);
    }

    public IReadOnlyList<string> ApprovalBlockers(ModuleContext ctx, JsonNode payload)
    {
        var plan = Json.FromNode<SqlPlanPayload>(payload);
        var blockers = plan.Errors.Select(e => $"plan: {e}").ToList();
        foreach (var id in plan.Order.Where(plan.Tasks.ContainsKey))
            if (plan.Tasks[id].Errors.Count > 0) blockers.Add($"{id} ({plan.Tasks[id].Target}): {plan.Tasks[id].Errors.Count} validation error(s)");
        var orderSet = new HashSet<string>(plan.Order, StringComparer.Ordinal);
        if (plan.Order.Count != plan.Tasks.Count || !orderSet.SetEquals(plan.Tasks.Keys))
            blockers.Add("order must list every task id exactly once");
        return blockers;
    }

    public string Summarize(JsonNode payload)
    {
        var plan = Json.FromNode<SqlPlanPayload>(payload);
        var errors = plan.ErrorCount();
        return $"{plan.Tasks.Count} tasks, {plan.Tasks.Values.Count(t => t.Custom)} custom, {plan.WarningCount()} warnings"
            + (errors > 0 ? $", {errors} errors" : "");
    }

    /// <summary>"task:T04" → (T04, null); "sql:T04:12" → (T04, 12); anything else → (null, null).</summary>
    public static (string? TaskId, int? Line) ParseAnchor(string? anchor)
    {
        if (anchor is null) return (null, null);
        if (anchor.StartsWith("task:", StringComparison.Ordinal)) return (anchor[5..], null);
        if (anchor.StartsWith("sql:", StringComparison.Ordinal))
        {
            var rest = anchor[4..];
            var colon = rest.LastIndexOf(':');
            if (colon > 0 && int.TryParse(rest[(colon + 1)..], out var line)) return (rest[..colon], line);
        }
        return (null, null);
    }

    static JsonObject Detail(string id, TaskPlan t, MappingPayload? mapping, CatalogSnapshot? tgt)
    {
        var p = $"/tasks/{id}";
        var o = new JsonObject
        {
            ["id"] = id,
            ["target"] = t.Target,
            ["mode"] = t.Mode,
            ["keys"] = Array(t.KeyColumns),
            ["identityInsert"] = t.IdentityInsert,
            ["chunkSize"] = t.ChunkSize,
            ["custom"] = t.Custom,
            ["dependsOn"] = Array(t.DependsOn),
            ["cols"] = Array(t.Columns.Select(ColumnText)),
            ["sourceQuery"] = t.SourceQuery,
            ["stagingDdl"] = t.StagingDdl,
            ["mergeSql"] = t.MergeSql,
            ["preSql"] = Array(t.PreSql),
            ["postSql"] = Array(t.PostSql),
            ["errors"] = Array(t.Errors),
            ["warnings"] = Array(t.Warnings),
            ["paths"] = new JsonObject
            {
                ["sourceQuery"] = p + "/sourceQuery", ["stagingDdl"] = p + "/stagingDdl", ["mergeSql"] = p + "/mergeSql",
                ["preSql"] = p + "/preSql", ["postSql"] = p + "/postSql", ["columns"] = p + "/columns", ["keyColumns"] = p + "/keyColumns",
            },
        };
        if (mapping is not null && FindTableMap(mapping, t.Target) is { } map) o["tableMap"] = WithoutCandidates(map);
        if (tgt?.FindTable(t.Target) is { } table)
            o["targetColumns"] = Array(table.Columns.OrderBy(c => c.Ordinal).Select(c =>
                $"{c.Name} {SqlTypeText.Format(c)} {(c.IsNullable ? "NULL" : "NOT NULL")}"
                + (c.IsIdentity ? " IDENTITY" : "") + (c.IsComputed ? " COMPUTED" : "") + (c.IsRowVersion ? " ROWVERSION" : "")
                + (c.DefaultDefinition is { Length: > 0 } d ? " DEFAULT " + d : "")));
        return o;
    }

    static JsonObject Legend() => new()
    {
        ["work"] = "tasks you must fix or complete, in full",
        ["others"] = "tasks that validated cleanly, summarised; `dbm artifact sql --path /tasks/<id>` prints one in full",
        ["context"] = "rework only: per feedback id, the anchored task in full; for sql:<id>:<line> anchors also the numbered listing ('>>' marks the line)",
        ["servers"] = "source/target product version, major version, edition, database compatibility level and collation; write T-SQL both accept",
        ["cols"] = "column bindings; 'alias->Target' when the SourceQuery alias differs from the target column",
        ["keys"] = "key aliases (__k0, __k1, ...) selected in SourceQuery; empty = single-transaction load",
        ["sourceQuery"] = "SELECT <expr> AS [TargetColumn], ..., s.[k] AS [__kN] FROM ... [WHERE ...]; runs on the SOURCE; no ORDER BY, no TOP",
        ["stagingDdl"] = "staging_merge only: CREATE TABLE #stg (...); runs on the TARGET before each chunk is bulk-copied into #stg",
        ["mergeSql"] = "staging_merge only: moves #stg rows into the target (INSERT...SELECT or MERGE ... ;), same transaction as the chunk",
        ["pre"] = "global statements on the TARGET before the first task (patch path /preSql)",
        ["post"] = "global statements on the TARGET after the last task (patch path /postSql)",
        ["targetColumns"] = "name type NULL|NOT NULL [IDENTITY|COMPUTED|ROWVERSION] [DEFAULT expr]",
        ["tableMap"] = "the approved mapping for this target; expressions are over the FROM aliases (s = primary source)",
        ["paths"] = "JSON pointers for patch ops on this task",
    };

    static JsonNode? ServerNode(ServerMeta? m) => m is null ? null : new JsonObject
    {
        ["version"] = m.ProductVersion,
        ["major"] = m.MajorVersion,
        ["edition"] = m.Edition,
        ["compatLevel"] = m.CompatLevel,
        ["collation"] = m.DatabaseCollation,
    };

    static string ColumnText(ColumnBinding b) => b.Source == b.Target ? b.Target : $"{b.Source}->{b.Target}";

    static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    static TableMap? FindTableMap(MappingPayload mapping, string target)
    {
        if (mapping.Tables.TryGetValue(target, out var exact)) return exact;
        return mapping.Tables.FirstOrDefault(kv => string.Equals(kv.Key, target, StringComparison.OrdinalIgnoreCase)).Value;
    }

    static JsonNode WithoutCandidates(TableMap map)
    {
        var node = Json.ToNode(map).AsObject();
        node.Remove("candidates");
        if (node["columns"] is JsonObject cols)
            foreach (var (_, col) in cols)
                (col as JsonObject)?.Remove("candidates");
        return node;
    }

    static bool SqlChanged(TaskPlan a, TaskPlan b)
    {
        static string N(string? s) => (s ?? "").Replace("\r\n", "\n").TrimEnd();
        static bool Same(List<string> x, List<string> y) => x.Select(N).SequenceEqual(y.Select(N), StringComparer.Ordinal);
        return N(a.SourceQuery) != N(b.SourceQuery) || N(a.StagingDdl) != N(b.StagingDdl) || N(a.MergeSql) != N(b.MergeSql)
            || !Same(a.PreSql, b.PreSql) || !Same(a.PostSql, b.PostSql) || a.Mode != b.Mode
            || !a.KeyColumns.SequenceEqual(b.KeyColumns, StringComparer.Ordinal) || !a.Columns.SequenceEqual(b.Columns)
            || a.IdentityInsert != b.IdentityInsert || a.ChunkSize != b.ChunkSize;
    }

    /// <summary>Normalises the payload in place (Custom flags, CountSql, validation results) so the stored version carries them.</summary>
    static void ReplaceContent(JsonNode payload, SqlPlanPayload plan)
    {
        var target = payload.AsObject();
        var fresh = Json.ToNode(plan).AsObject();
        target.Clear();
        foreach (var key in fresh.Select(p => p.Key).ToList())
        {
            var value = fresh[key];
            fresh.Remove(key);
            target[key] = value;
        }
    }
}
```

`Validate` blocks on the async validator with `GetAwaiter().GetResult()` because `IPhaseModule.Validate` is synchronous (C5). That is safe here: neither the CLI nor ASP.NET Core has a synchronization context, and the call is bounded by a 3-minute timeout.

- [ ] **Step 4: Implement the script pack and its exports**

`plugins/db-migrate/engine/Dbm/Core/SqlGen/ScriptPack.cs`:

```csharp
using System.IO.Compression;
using System.Text;

namespace Dbm.Core.SqlGen;

/// <summary>DBA-readable export of a plan: 00_pre.sql, one NN_schema_table.sql per task (execution order), 99_post.sql, README.md.</summary>
public static class ScriptPack
{
    const string Rule = "-- =====================================================================";

    public static byte[] BuildZip(SqlPlanPayload plan, string projectName)
    {
        var files = BuildFiles(plan, projectName);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    /// <summary>File name → content, in zip order.</summary>
    public static List<(string Name, string Content)> BuildFiles(SqlPlanPayload plan, string projectName)
    {
        var width = Math.Max(2, (plan.Order.Count + 1).ToString().Length);
        var preName = new string('0', width) + "_pre.sql";
        var postName = new string('9', width) + "_post.sql";
        var files = new List<(string, string)> { (preName, GlobalFile(projectName, preName, "pre-load", "before the first task", plan.PreSql)) };
        var index = new List<(string File, string Id, TaskPlan Task)>();
        for (var i = 0; i < plan.Order.Count; i++)
        {
            if (!plan.Tasks.TryGetValue(plan.Order[i], out var task)) continue;
            var name = $"{(i + 1).ToString("D" + width)}_{FileSafe(task.Target.Replace('.', '_'))}.sql";
            files.Add((name, TaskFile(projectName, plan.Order[i], task)));
            index.Add((name, plan.Order[i], task));
        }
        files.Add((postName, GlobalFile(projectName, postName, "post-load", "after the last task", plan.PostSql)));
        files.Add(("README.md", Readme(projectName, preName, postName, index)));
        return files;
    }

    static string GlobalFile(string project, string name, string what, string when, List<string> statements)
    {
        var sb = new StringBuilder();
        sb.Append(Rule).Append('\n')
          .Append($"-- {project}: {name} - global {what} statements\n")
          .Append($"-- runs on TARGET, {when}\n")
          .Append(Rule).Append("\n\n");
        if (statements.Count == 0) sb.Append($"-- (no global {what} statements)\n");
        foreach (var s in statements) sb.Append(s.TrimEnd()).Append("\nGO\n\n");
        return sb.ToString();
    }

    static string TaskFile(string project, string id, TaskPlan t)
    {
        var sb = new StringBuilder();
        sb.Append(Rule).Append('\n')
          .Append($"-- {project}: task {id}  {t.Target}\n")
          .Append($"-- Mode:            {t.Mode}\n")
          .Append($"-- Keys:            {(t.KeyColumns.Count == 0 ? "(none - the table loads in a single transaction)" : string.Join(", ", t.KeyColumns))}\n")
          .Append($"-- Depends on:      {(t.DependsOn.Count == 0 ? "(none)" : string.Join(", ", t.DependsOn))}\n")
          .Append($"-- Identity insert: {(t.IdentityInsert ? "yes" : "no")}\n")
          .Append($"-- Chunk size:      {(t.ChunkSize?.ToString() ?? "run default")}\n")
          .Append($"-- Custom SQL:      {(t.Custom ? "yes" : "no")}\n");
        AppendList(sb, "Warnings", t.Warnings);
        AppendList(sb, "Errors (last validation)", t.Errors);
        sb.Append(Rule).Append("\n\n");

        if (t.PreSql.Count > 0)
        {
            sb.Append("-- ---- Task pre-load ----\n-- runs on TARGET\n");
            foreach (var s in t.PreSql) sb.Append(s.TrimEnd()).Append("\nGO\n");
            sb.Append('\n');
        }

        sb.Append("-- ---- Source query ----\n-- runs on SOURCE\n")
          .Append("-- dbm reads this result set on the source server and streams it to the target with SqlBulkCopy.\n")
          .Append("-- Chunks are keyset ranges on the key aliases; aliases that are not target columns are not loaded.\n")
          .Append(t.SourceQuery.TrimEnd()).Append("\nGO\n\n");

        sb.Append("-- ---- Load ----\n-- runs on TARGET\n");
        var map = string.Join(", ", t.Columns.Select(c => c.Source == c.Target ? c.Target : $"{c.Source} -> {c.Target}"));
        if (t.Mode == "staging_merge")
        {
            sb.Append("-- Each chunk is bulk-copied into #stg, then the merge statement runs in the same transaction.\n")
              .Append($"-- Columns: {map}\n")
              .Append((t.StagingDdl ?? "-- (missing staging DDL)").TrimEnd()).Append("\nGO\n")
              .Append((t.MergeSql ?? "-- (missing merge SQL)").TrimEnd()).Append("\nGO\n\n");
        }
        else
        {
            sb.Append($"-- Each chunk is bulk-copied straight into {t.Target}{(t.IdentityInsert ? " (KeepIdentity)" : "")}.\n")
              .Append($"-- Columns: {map}\n\n");
        }

        if (t.PostSql.Count > 0)
        {
            sb.Append("-- ---- Task post-load ----\n-- runs on TARGET\n");
            foreach (var s in t.PostSql) sb.Append(s.TrimEnd()).Append("\nGO\n");
            sb.Append('\n');
        }

        sb.Append("-- ---- Validation ----\n-- runs on SOURCE: expected row count, compared with the rows the run added to the target\n")
          .Append(t.CountSql.TrimEnd()).Append(";\nGO\n");
        return sb.ToString();
    }

    static void AppendList(StringBuilder sb, string title, List<string> items)
    {
        if (items.Count == 0) return;
        sb.Append($"-- {title}:\n");
        foreach (var i in items) sb.Append("--   - ").Append(i.Replace("\n", " ")).Append('\n');
    }

    static string Readme(string project, string preName, string postName, List<(string File, string Id, TaskPlan Task)> index)
    {
        var sb = new StringBuilder();
        sb.Append($"# {project} - migration script pack\n\n")
          .Append("Generated by **dbm** from the SQL plan. These files are for review, audit and rehearsal; the dbm engine runs the migration itself.\n\n")
          .Append("## How dbm moves the data\n\n")
          .Append("Source and target may be on different servers and **no linked server is used**. For every task dbm runs the *source query* on the\n")
          .Append("source server, streams the rows through its own process and writes them to the target with `SqlBulkCopy`. Scripts marked\n")
          .Append("`-- runs on SOURCE` are executed on the source database; everything else runs on the target database.\n\n")
          .Append("## Execution order\n\n")
          .Append($"1. `{preName}` - global statements on the target (for example disabling FK constraints that form a cycle).\n")
          .Append("2. One file per task, in file-name order. A task starts only when the tasks in its *Depends on* list have finished;\n")
          .Append("   independent tasks run in parallel. Inside a task: task pre-load -> source query -> load -> task post-load.\n")
          .Append("   - `direct`: each chunk is bulk-copied straight into the target table (identity values kept when *Identity insert* is yes).\n")
          .Append("   - `staging_merge`: each chunk is bulk-copied into the session table `#stg`, then the merge statement moves it into the target\n")
          .Append("     in the same transaction.\n")
          .Append($"3. `{postName}` - global statements after the last task (for example re-enabling constraints `WITH CHECK`).\n\n")
          .Append("## Chunks, checkpoints and resume\n\n")
          .Append("The key aliases (`__k0`, `__k1`, ...) are the source key. dbm reads keyset chunks (`WHERE key > last ORDER BY key`) and commits\n")
          .Append("each chunk together with its checkpoint row in `dbo.__dbm_checkpoint` on the target, so a paused or crashed run resumes at the\n")
          .Append("last committed chunk. Tasks without a key load in a single transaction and restart from scratch.\n\n")
          .Append("## Validation\n\n")
          .Append("Each task file ends with a row-count query that runs on the source; dbm compares it with the rows the run added to the target.\n\n")
          .Append("## Files\n\n")
          .Append("| File | Task | Target | Mode | Depends on |\n|---|---|---|---|---|\n");
        foreach (var (file, id, t) in index)
            sb.Append($"| `{file}` | {id} | {t.Target} | {t.Mode} | {(t.DependsOn.Count == 0 ? "-" : string.Join(", ", t.DependsOn))} |\n");
        return sb.ToString();
    }

    static string FileSafe(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        return new string(chars);
    }
}
```

`plugins/db-migrate/engine/Dbm/Web/Endpoints/SqlExports.cs`:

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Microsoft.AspNetCore.Routing;

namespace Dbm.Web.Endpoints;

/// <summary>Registers the SQL exports with ExportEndpoints (T2.8), for the sql phase's current version:
/// <c>sqlpack</c> → the DBA script pack (zip), <c>sql</c> → the standalone read-only SQL screen (HTML; needs views/sql.js from T4.5).</summary>
public static class SqlExports
{
    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        ExportEndpoints.Register("sqlpack", (s, _, _) => Task.FromResult(Pack(s.Services)));
        ExportEndpoints.Register("sql", (s, assets, _) => Task.FromResult(Html(s.Services, assets)));
    }

    /// <summary>"&lt;project&gt;-sql-v&lt;n&gt;.zip"; ExportException when the phase has no version yet.</summary>
    public static ExportFile Pack(DbmServices services)
    {
        var row = Current(services);
        var project = services.Project.Get().Name;
        var bytes = ScriptPack.BuildZip(Json.Deserialize<SqlPlanPayload>(row.PayloadJson), project);
        return new ExportFile($"{Safe(project)}-sql-v{row.Version}.zip", "application/zip", bytes);
    }

    /// <summary>Standalone HTML of the SQL screen: payload {project, exportedAt, artifact:{version,author,summary,createdAt,payload}}.</summary>
    public static ExportFile Html(DbmServices services, IWebAssets assets)
    {
        var row = Current(services);
        var project = services.Project.Get().Name;
        var data = new JsonObject
        {
            ["project"] = project,
            ["exportedAt"] = Clock.NowText(),
            ["artifact"] = new JsonObject
            {
                ["version"] = row.Version,
                ["author"] = row.Author,
                ["summary"] = row.Summary,
                ["createdAt"] = row.CreatedAt.ToString("O"),
                ["payload"] = JsonNode.Parse(row.PayloadJson),
            },
        };
        var html = WebExport.BuildHtml(assets, "sql", $"{project} — SQL v{row.Version}", data);
        return new ExportFile($"sql-v{row.Version}.html", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
    }

    private static ArtifactRow Current(DbmServices services) =>
        SqlPlanSource.CurrentRow(services) ?? throw new ExportException("No SQL plan yet.");

    private static string Safe(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var s = new string(chars).Trim('-');
        return s.Length == 0 ? "dbm" : s;
    }
}
```

`plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs` (T1.7) — inside `MapAll`, directly after the line `        MappingEndpoints.Map(app, state);   // T3.5`, add:

```csharp
        SqlExports.Map(app, state);         // T4.4
```

`ExportEndpoints` resolves the kind per request, so the registration order relative to `ExportEndpoints.Map` does not matter; `TokenGuard` (T1.7) already accepts `?t=` on GET, which is what the download link uses.

- [ ] **Step 5: Register the module**

`plugins/db-migrate/engine/Dbm/Core/ModuleRegistry.cs` — below the `// milestone registrations below` marker, directly after the line `        yield return new Dbm.Core.Mapping.MappingModule(s);    // T3.4`, add:

```csharp
        yield return new Dbm.Core.SqlGen.SqlModule();      // T4.4
```

- [ ] **Step 6: Write the sql-engineer agent**

`plugins/db-migrate/agents/sql-engineer.md`:

````markdown
---
name: sql-engineer
description: Refines the db-migrate SQL plan - fixes validation errors, writes staging/MERGE SQL for lookup tasks and answers review feedback on SQL tasks. Dispatched by the /db-migrate orchestrator when `dbm next` returns {"action":"agent","agent":"sql-engineer"}; the prompt contains the work packet path.
tools: Bash, Read, Write
model: inherit
---

You are the SQL engineer of a db-migrate project. The engine (`dbm`) has generated a migration plan from the approved
mapping: one task per target table, in FK-dependency order. Your job is judgement only: make every task valid and
correct, write the SQL the generator cannot, and answer the reviewer. You never move data and never touch the databases
except through `dbm`.

## 1. Read the packet

Read the packet file named in your prompt (one Read). Envelope: `phase` ("sql"), `mode` ("draft" | "rework"),
`baseVersion`, `patchPath`, `feedback` ([{id, anchor, text}], rework only), `rules`, `data`. Inside `data`:

- `legend` — meaning of every key; read it first.
- `servers` — source/target version, edition, compatibility level, collation. All SQL you write must run on both the
  source (source queries) and the target (everything else) at those versions.
- `order`, `pre`, `post`, `planErrors` — execution order and global target statements.
- `work` — tasks you must handle, in full: `sourceQuery`, `stagingDdl`, `mergeSql`, `preSql`, `postSql`, `errors`,
  `warnings`, `cols`, `keys`, `paths` (JSON pointers for your patch), `tableMap` (the approved mapping) and
  `targetColumns`.
- `others` — tasks that are fine, summarised. `dbm artifact sql --path /tasks/<id>` prints one in full if you need it.
- `context` (rework) — per feedback id: the anchored task in full; for `sql:<task>:<line>` anchors also `line`,
  `section` and a numbered `listing` where `>>` marks the commented line.

More context when needed: `dbm show <schema.table> --side src|tgt` (columns, keys, FKs) and
`dbm search "<words>" --side src|tgt -k 5`. Do not read anything under `.dbmigrate/` except the packet you were
given — never `state.db` — and never print or look for connection strings.

## 2. How the engine runs a task (so your SQL fits)

1. Task `preSql` runs on the TARGET (after the global `pre`).
2. `sourceQuery` runs on the SOURCE. dbm wraps it for keyset chunking (it adds its own `WHERE`/`ORDER BY` on the key
   aliases around your query) and streams the rows with SqlBulkCopy — no linked server.
3. `direct`: rows are bulk-copied straight into the target table, columns matched by the aliases in `cols`.
   `staging_merge`: per chunk, `stagingDdl` creates `#stg` on the target, the rows are bulk-copied into `#stg`, then
   `mergeSql` moves them into the target — all in the chunk's transaction.
4. Task `postSql` runs on the TARGET (before the global `post`). The count query is derived from `sourceQuery`.

## 3. Hard rules

- **SourceQuery shape:** `SELECT <expr> AS [TargetColumn], …, <key expr> AS [__k0][, … AS [__k1]] FROM … [WHERE …]`.
  One SELECT statement; every alias in `cols` and every key alias in `keys` must be in the select list exactly once;
  **no ORDER BY, no TOP/OFFSET, no INTO, no temp tables, no variables, no trailing GO**. Keep the key aliases selecting
  the primary source's key unless the task truly has no key.
- Bracket every identifier (`[dbo].[CUST]`, `s.[CUST_NM]`), use `N'…'` for Unicode literals, alias the primary source `s`.
- **Never change the target schema.** The only DDL allowed is `CREATE TABLE #stg (…)` in `stagingDdl`. In `preSql` /
  `postSql` (task or global) you may only use `ALTER TABLE … NOCHECK CONSTRAINT …`, `ALTER TABLE … WITH CHECK CHECK
  CONSTRAINT …`, `ALTER TABLE … DISABLE|ENABLE TRIGGER …`, `UPDATE STATISTICS …` and DML against `#stg`. No DROP,
  TRUNCATE, CREATE INDEX, ALTER COLUMN, or cross-database/linked-server references.
- **staging_merge tasks:** `stagingDdl` declares exactly the source query's columns (target column aliases + key aliases),
  all `NULL`, target types. `mergeSql` reads `#stg` (and target tables for lookups), writes only the task's target,
  and ends every `MERGE` with `;`. Replace the generated `INSERT … SELECT … FROM #stg;` skeleton with the real logic the
  `tableMap` asks for (lookups, de-duplication, `WHEN MATCHED` updates when the target may already hold the row).
- No `GO` anywhere; each list element of `preSql`/`postSql` is one self-contained batch.
- Change `mode`, `columns` and `keyColumns` only together and consistently. Never patch `errors`, `warnings`, `custom`,
  `countSql` or `mappingHash` — the engine owns them. Warnings prefixed `validate:` are recomputed on every check.
- Warnings are information, not failures. Do not twist SQL to silence a truncation or nullability warning — mention
  it in your summary unless the mapping or the reviewer asked for a transform.

## 4. Do the work

- **Draft:** fix every error in `work` and `planErrors`; complete every `staging_merge` task.
- **Rework:** respond to **every** feedback id — `addressed` with a note saying what changed, or `declined` with the
  reason. Change only what the feedback asks for (plus errors you find in the same task).

Write the patch to `patchPath`:

```json
{"phase":"sql","baseVersion":<baseVersion>,
 "ops":[{"op":"replace","path":"/tasks/T04/sourceQuery","value":"SELECT\n    s.[ADDR_ID] AS [AddressId], ...\nFROM [dbo].[ADDR] AS s\nWHERE (s.[CITY] <> '')"}],
 "responses":[{"feedbackId":12,"status":"addressed","note":"Addresses without a city are filtered out in T04."}],
 "summary":"T04 filters blank cities; T05 lookup merge written."}
```

Use `replace` for fields that exist, `add` for absent optional fields (`stagingDdl`/`mergeSql` on a direct task, a new
list element with `/preSql/-`), `remove` to clear an optional field. Paths are in each task's `paths`.

## 5. Check before you finish

1. `dbm sql validate --patch <patchPath>` (add `--task <id>` to focus on one task). It prints
   `{"ok":…,"version":…,"patched":true,"taskErrors":{…},"taskWarnings":{…},"globalErrors":[…]}` and exits 1 while
   `"ok"` is false. Fix and repeat until `"ok":true`.
2. `dbm apply <patchPath> --dry-run` — must print `"ok":true` (it re-validates live and checks that every feedback id has
   a response). Fix and repeat until it does. Do **not** run `dbm apply` without `--dry-run`; the orchestrator applies.

## 6. Reply

Exactly one line: `<patchPath> — <n> ops, <m> responses, validate ok`.
````

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.SqlGen.SqlModuleTests|FullyQualifiedName~Dbm.Tests.Unit.SqlGen.ScriptPackTests|FullyQualifiedName~Dbm.Tests.Unit.Web.SqlExportsTests"`
Expected: `Passed!  - Failed: 0, Passed: 24` (15 module incl. 5 theory rows + 6 script pack + 3 exports).

Run the whole unit suite to catch registry regressions: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "Category!=Integration"` — Expected: no failures.

- [ ] **Step 8: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/Core/SqlGen/TaskListing.cs plugins/db-migrate/engine/Dbm/Core/SqlGen/SqlModule.cs plugins/db-migrate/engine/Dbm/Core/SqlGen/ScriptPack.cs plugins/db-migrate/engine/Dbm/Web/Endpoints/SqlExports.cs plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs plugins/db-migrate/engine/Dbm/Core/ModuleRegistry.cs plugins/db-migrate/agents/sql-engineer.md plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/SqlModuleTests.cs plugins/db-migrate/engine/Dbm.Tests/Unit/SqlGen/ScriptPackTests.cs plugins/db-migrate/engine/Dbm.Tests/Unit/Web/SqlExportsTests.cs
git commit -F - <<'EOF'
feat(sqlgen): add SQL phase module, sql-engineer agent and script pack export

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

### Task 4.5: SQL UI (highlighter, line comments, edit, diff, validate, script pack)

**Files:**
- Create: `plugins/db-migrate/engine/Dbm/wwwroot/js/lib/highlight.js`
- Create: `plugins/db-migrate/engine/Dbm/wwwroot/js/views/sql.js`
- Create: `plugins/db-migrate/engine/Dbm/Web/Endpoints/SqlEndpoints.cs`
- Modify: `plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs` (T1.7) — one line
- Modify: `plugins/db-migrate/engine/Dbm/wwwroot/index.html` (T1.9) — two script tags
- Modify: `plugins/db-migrate/engine/Dbm/wwwroot/css/app.css` (T1.9) — append the SQL view block
- Test: `plugins/db-migrate/engine/Dbm.Tests/js/highlight.test.cjs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/js/sql-view.test.cjs`
- Test: `plugins/db-migrate/engine/Dbm.Tests/Unit/Web/SqlEndpointsTests.cs`

**Interfaces:**
- Consumes (T1.9 UI shell): `DBM.h(tag, attrs, …children)` (null/false attributes and children are skipped; `html` sets trusted markup), `DBM.fmt.num`, `DBM.components.{reviewBar(ctx), kpi(label, value, sub), emptyState(title, text)}`, `DBM.diff.lines(a, b)` (two strings → `[{op:'eq'|'add'|'del', text}]`; `.diff-add/.diff-del` draw their own `+`/`−` markers), and the view context from `app.js` `makeCtx`: `artifact` = the version being shown `{version, author, summary, createdAt, payload}`, `versions` ([ArtifactMeta] ascending), `latestVersion`, `version`, `readOnly` (true unless the phase awaits review and the current version is shown), `api` (`DBM.api`: `get`, `post`, `url(path)` adds `?t=<token>`), `toast(message, 'ok'|'err'|'info')`, `commentable(el, anchor, label)`, `refresh()`; in standalone exports `api` is `null`, `versions` is `[]` and `readOnly` is true. CSS vocabulary from `app.css`: `.page .page-h .stack .row .row-wrap .spacer .toolbar .grid-kpi .card .card-h .card-b .muted .small .mono .ellipsis .h1 .h2 .h3 .btn .btn-primary .btn-ghost .btn-sm .textarea .select .tbl .tbl-compact .chip .tag .pill .badge .st-<status> .code .ln .line .tok-* .diff .diff-add .diff-del .diff-eq .is-active .is-loading`. Server: `POST /api/edit/{phase}` (T1.7; 200 with `ApplyResult`, `ok:false` + `errors` when rejected), `GET /api/artifact/sql/{version}` (T1.7), `ApiResults.Json/Error` (T1.7), the T4.4 exports, `SqlPlanSource` (T4.3). Tests: `WebTestServer.StartAsync(ws, factory)`, `SendAsync` (M1), `SqlExportsTests.WithPlan` (T4.4).
- Produces:
  - `DBM.highlight = { sql(text) → escaped HTML string, sqlLines(text) → string[] (one escaped HTML string per line; tokens spanning lines are closed and reopened), tokenize(text) → [{c, v}] }` with classes `tok-kw tok-str tok-num tok-com tok-id tok-fn tok-op`.
  - `DBM.views.sql = { title: 'SQL', render(root, ctx) }` and `DBM.sqlView = { listing, listingText, splitStatements, joinStatements, editOps }` (pure helpers; `listing` = the TaskListing rules of T4.4).
  - `public static class SqlEndpoints { void Map(IEndpointRouteBuilder app, WebState state); }` → `POST /api/sql/validate` body `{task?}` → `{version, ok, taskErrors, taskWarnings, globalErrors}`; 409 `{error:"no_version"|"not_ready"}`; 400 `{error:"bad_request"}`. Nothing is stored.

**Screen (C9 visual direction, view classes prefixed `sql-`):** page header "Migration SQL" with **Validate live** and **Download script pack** (the pack of the current version, via `ctx.api.url('/api/export/sqlpack')`); the review bar; KPI row (tasks, custom, warnings, errors); the live validation report (dismissable, task chips jump to the task); plan notes; **Global pre-load** card; a two-column layout — task list in execution order (id, target, mode tag, custom chip, error/warning pills, "after T02, T04") and the task detail (header commentable as `task:<id>`, meta tags incl. clickable depends-on chips, errors/warnings, collapsible column bindings, one highlighted code block per section with line numbers continuing across sections and **every line commentable** as `sql:<id>:<line>`, collapsible count query); **Global post-load** card. **Edit SQL** (only when `ctx.readOnly` is false) swaps the code for one monospace textarea per SQL field (pre/post statements separated by `GO` lines) and saves with `POST /api/edit/sql` (replace/add/remove ops). **Compare with…** loads any other version and shows a line diff of this task's listing. Below 760 px the task list becomes a horizontal strip above the detail.

- [ ] **Step 1: Write the failing JS tests**

`plugins/db-migrate/engine/Dbm.Tests/js/highlight.test.cjs`:

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

vm.runInThisContext(fs.readFileSync(path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js', 'lib', 'highlight.js'), 'utf8'));
const H = globalThis.DBM.highlight;

test('keywords, functions, bracketed identifiers and numbers', () => {
  const html = H.sql('SELECT LEFT(s.[CUST_NM], 3) AS [First] FROM [dbo].[CUST] AS s');
  assert.equal(html,
    '<span class="tok-kw">SELECT</span> <span class="tok-fn">LEFT</span>(s.<span class="tok-id">[CUST_NM]</span>, ' +
    '<span class="tok-num">3</span>) <span class="tok-kw">AS</span> <span class="tok-id">[First]</span> ' +
    '<span class="tok-kw">FROM</span> <span class="tok-id">[dbo]</span>.<span class="tok-id">[CUST]</span> ' +
    '<span class="tok-kw">AS</span> s');
});

test('a script tag inside a string literal comes out escaped', () => {
  const html = H.sql("SELECT '<script>alert(1)</script>' AS x");
  assert.ok(!html.includes('<script>'));
  assert.ok(html.includes('<span class="tok-str">&#39;&lt;script&gt;alert(1)&lt;/script&gt;&#39;</span>'));
});

test('doubled quotes stay inside one string; N prefix belongs to the string', () => {
  const html = H.sql("SELECT N'O''Brien', 'x'");
  assert.ok(html.includes('<span class="tok-str">N&#39;O&#39;&#39;Brien&#39;</span>'));
  assert.ok(html.includes('<span class="tok-str">&#39;x&#39;</span>'));
});

test('line comments end at the newline and are escaped', () => {
  const lines = H.sqlLines("SELECT 1 -- it's <b>bold</b>\nFROM t");
  assert.equal(lines[0], '<span class="tok-kw">SELECT</span> <span class="tok-num">1</span> <span class="tok-com">-- it&#39;s &lt;b&gt;bold&lt;/b&gt;</span>');
  assert.equal(lines[1], '<span class="tok-kw">FROM</span> t');
});

test('block comments (nested) spanning lines are reopened on every line', () => {
  const lines = H.sqlLines('/* a /* b */\n c */ SELECT');
  assert.deepEqual(lines, ['<span class="tok-com">/* a /* b */</span>', '<span class="tok-com"> c */</span> <span class="tok-kw">SELECT</span>']);
});

test('brackets with an escaped closing bracket are one identifier', () => {
  assert.equal(H.sql('[a]]b] + 1'), '<span class="tok-id">[a]]b]</span> <span class="tok-op">+</span> <span class="tok-num">1</span>');
});

test('variables and temp tables are identifiers; <> is one operator', () => {
  assert.equal(H.sql('@x <> #stg'), '<span class="tok-id">@x</span> <span class="tok-op">&lt;&gt;</span> <span class="tok-id">#stg</span>');
});

test('a function name without a parenthesis is a keyword or plain text', () => {
  assert.equal(H.sql('nvarchar(max)'), '<span class="tok-kw">nvarchar</span>(<span class="tok-kw">max</span>)');
  assert.equal(H.sql('LEFT JOIN'), '<span class="tok-kw">LEFT</span> <span class="tok-kw">JOIN</span>');
  assert.equal(H.sql('MAX (x)'), '<span class="tok-fn">MAX</span> (x)');
});

test('unterminated strings and brackets do not throw', () => {
  assert.equal(H.sql("SELECT '<x"), '<span class="tok-kw">SELECT</span> <span class="tok-str">&#39;&lt;x</span>');
  assert.equal(H.sql('[open'), '<span class="tok-id">[open</span>');
});

test('line count is preserved; CRLF normalised; null is empty', () => {
  assert.equal(H.sqlLines('a\r\n\r\nb').length, 3);
  assert.equal(H.sql(null), '');
  assert.equal(H.sql(undefined), '');
});

test('tokens concatenate back to the input', () => {
  const src = "SELECT N'x''y' /* c */ -- d\n, [a]]b], 0x1F, 1.5e3 FROM t WHERE a<>b";
  assert.equal(H.tokenize(src).map(t => t.v).join(''), src);
});
```

`plugins/db-migrate/engine/Dbm.Tests/js/sql-view.test.cjs`:

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

vm.runInThisContext(fs.readFileSync(path.join(__dirname, '..', '..', 'Dbm', 'wwwroot', 'js', 'views', 'sql.js'), 'utf8'));
const V = globalThis.DBM.sqlView;

const addresses = {
  target: 'app.Addresses',
  preSql: [],
  sourceQuery: 'SELECT\n    s.[ADDR_ID] AS [AddressId],\n    s.[CUST_ID] AS [CustomerId],\n    s.[LINE1] AS [Line1],\n' +
    '    s.[CITY] AS [City],\n    s.[ZIP] AS [PostalCode],\n    s.[CTRY_CD] AS [CountryCode],\n    s.[ADDR_ID] AS [__k0]\nFROM [dbo].[ADDR] AS s',
  postSql: [],
};

test('listing numbers lines like TaskListing (sample Addresses task: line 9 is FROM)', () => {
  const lines = V.listing(addresses);
  assert.equal(lines.length, 9);
  assert.deepEqual(lines[8], { no: 9, section: 'source', text: 'FROM [dbo].[ADDR] AS s' });
});

test('listing order is pre, source, staging, merge, post; empty sections skipped; CRLF normalised', () => {
  const task = { preSql: ['ALTER A;'], sourceQuery: 'SELECT\r\n1 AS [x]', stagingDdl: null, mergeSql: '', postSql: ['P1;', 'P2a\nP2b'] };
  const lines = V.listing(task);
  assert.deepEqual(lines.map(l => l.no), [1, 2, 3, 4, 5, 6]);
  assert.deepEqual(lines.map(l => l.section), ['pre', 'source', 'source', 'post', 'post', 'post']);
  assert.equal(lines[2].text, '1 AS [x]');
});

test('splitStatements splits on GO lines only', () => {
  assert.deepEqual(V.splitStatements('A;\nGO\n\nB;\n  go  \nGOTO x;'), ['A;', 'B;', 'GOTO x;']);
});

test('joinStatements and splitStatements round-trip', () => {
  const list = ['ALTER TABLE [a].[b] NOCHECK CONSTRAINT [c];', 'UPDATE STATISTICS [a].[b];'];
  assert.deepEqual(V.splitStatements(V.joinStatements(list)), list);
});

test('editOps emits replace ops only for changed fields', () => {
  const task = { sourceQuery: 'SELECT 1 AS [a]', preSql: [], postSql: [] };
  const ops = V.editOps('T01', task, { sourceQuery: 'SELECT 2 AS [a]\n', preSql: 'X;\nGO\nY;', postSql: '', stagingDdl: '' });
  assert.deepEqual(ops, [
    { op: 'replace', path: '/tasks/T01/sourceQuery', value: 'SELECT 2 AS [a]' },
    { op: 'replace', path: '/tasks/T01/preSql', value: ['X;', 'Y;'] },
  ]);
});

test('editOps adds missing fields and removes cleared ones', () => {
  assert.deepEqual(V.editOps('T04', { sourceQuery: 'q' }, { stagingDdl: 'CREATE TABLE #stg (a int);' }),
    [{ op: 'add', path: '/tasks/T04/stagingDdl', value: 'CREATE TABLE #stg (a int);' }]);
  assert.deepEqual(V.editOps('T04', { sourceQuery: 'q', mergeSql: 'INSERT x;' }, { mergeSql: '  ' }),
    [{ op: 'remove', path: '/tasks/T04/mergeSql' }]);
});

test('the view is registered', () => {
  assert.equal(globalThis.DBM.views.sql.title, 'SQL');
  assert.equal(typeof globalThis.DBM.views.sql.render, 'function');
});
```

- [ ] **Step 2: Run the JS tests to verify they fail**

Run: `node --test plugins/db-migrate/engine/Dbm.Tests/js/highlight.test.cjs plugins/db-migrate/engine/Dbm.Tests/js/sql-view.test.cjs`
Expected: both files fail with `Error: ENOENT: no such file or directory, open '…\Dbm\wwwroot\js\lib\highlight.js'` (resp. `…\views\sql.js`).

- [ ] **Step 3: Implement the highlighter**

`plugins/db-migrate/engine/Dbm/wwwroot/js/lib/highlight.js` (library file: IIFE on `globalThis` so it also loads under Node; in the browser `globalThis === window`; T2.8's `WebExport.LibOrder` already lists `highlight.js`, so standalone exports inline it):

```js
/* DBM.highlight — tiny T-SQL highlighter. Output is escaped HTML; token classes: tok-kw tok-str tok-num tok-com tok-id tok-fn tok-op.
   Classic script; also runs under Node (globalThis) for `node --test`. */
(function (DBM) {
  'use strict';

  function wordSet(text) {
    var set = Object.create(null);
    text.split(/\s+/).forEach(function (w) { if (w) set[w] = true; });
    return set;
  }

  var KEYWORDS = wordSet(
    'add all alter and any as asc authorization begin between break bulk by cascade case catch check checkpoint close ' +
    'clustered collate column commit constraint contains continue create cross current current_date current_time ' +
    'current_timestamp current_user cursor database dbcc deallocate declare default delete deny desc disable distinct ' +
    'drop else enable end escape except exec execute exists exit fetch file fillfactor for foreign from full function ' +
    'go goto grant group having holdlock identity identity_insert if in index inner insert intersect into is join key ' +
    'kill left like merge matched national nocheck nocount nolock nonclustered not null of off offset on open option ' +
    'or order outer output over percent pivot primary print proc procedure raiserror read references return revoke ' +
    'right rollback rowcount rows schema select set source statistics table tablock target then throw to top tran ' +
    'transaction trigger truncate try union unique unpivot update use using values view waitfor when where while with ' +
    'xact_abort ' +
    'bigint binary bit char date datetime datetime2 datetimeoffset decimal float image int max money nchar ntext ' +
    'numeric nvarchar real rowversion smalldatetime smallint smallmoney sql_variant text time timestamp tinyint ' +
    'uniqueidentifier varbinary varchar xml');

  var FUNCTIONS = wordSet(
    'abs avg cast ceiling charindex choose coalesce concat concat_ws convert count count_big cume_dist dateadd datediff ' +
    'datediff_big datefromparts datename datepart datetime2fromparts day dense_rank eomonth exp first_value floor ' +
    'format getdate getutcdate hashbytes iif isdate isjson isnull isnumeric json_value lag last_value lead left len ' +
    'log lower ltrim max min month newid newsequentialid ntile nullif object_id parse patindex power quotename rank ' +
    'replace replicate reverse right round row_number rtrim scope_identity sign space sqrt str string_agg ' +
    'string_split stuff substring sum switchoffset sysdatetime sysdatetimeoffset sysutcdatetime todatetimeoffset ' +
    'translate trim try_cast try_convert try_parse upper year');

  var WORD = /[A-Za-z_@#À-￿][A-Za-z0-9_@#$À-￿]*/y;
  var NUMBER = /0x[0-9A-Fa-f]*|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?/y;
  var OPERATOR = /<>|<=|>=|!=|!<|!>|\+=|-=|\*=|\/=|%=|&=|\|=|\^=|[=<>!+\-*\/%&|^~]/y;

  function isWordChar(ch) { return !!ch && /[A-Za-z0-9_@#$À-￿]/.test(ch); }

  function stickyMatch(re, src, i) {
    re.lastIndex = i;
    var m = re.exec(src);
    return m ? m[0] : null;
  }

  /* Closing-delimiter scan where a doubled closer is an escape ('' in strings, ]] in brackets, "" in quoted ids).
     Unterminated tokens run to the end of the text. */
  function scanQuoted(src, start, closer) {
    var j = start;
    while (j < src.length) {
      if (src[j] === closer) {
        if (src[j + 1] === closer) { j += 2; continue; }
        return j + 1;
      }
      j++;
    }
    return src.length;
  }

  /* -> [{c: 'kw'|'str'|'num'|'com'|'id'|'fn'|'op'|null, v: text}] ; concatenating v gives back the input. */
  function tokenize(text) {
    var src = String(text == null ? '' : text);
    var out = [];
    var i = 0;
    var n = src.length;
    function push(cls, v) {
      if (!v) return;
      var last = out[out.length - 1];
      if (cls === null && last && last.c === null) { last.v += v; return; }
      out.push({ c: cls, v: v });
    }
    while (i < n) {
      var ch = src[i];
      var nx = src[i + 1];
      var j;
      if (ch === '-' && nx === '-') {
        j = src.indexOf('\n', i);
        if (j < 0) j = n;
        push('com', src.slice(i, j)); i = j; continue;
      }
      if (ch === '/' && nx === '*') {
        var depth = 1;
        j = i + 2;
        while (j < n && depth > 0) {
          if (src[j] === '/' && src[j + 1] === '*') { depth++; j += 2; }
          else if (src[j] === '*' && src[j + 1] === '/') { depth--; j += 2; }
          else j++;
        }
        push('com', src.slice(i, j)); i = j; continue;
      }
      if (ch === "'" || ((ch === 'N' || ch === 'n') && nx === "'" && !isWordChar(src[i - 1]))) {
        j = scanQuoted(src, ch === "'" ? i + 1 : i + 2, "'");
        push('str', src.slice(i, j)); i = j; continue;
      }
      if (ch === '[') { j = scanQuoted(src, i + 1, ']'); push('id', src.slice(i, j)); i = j; continue; }
      if (ch === '"') { j = scanQuoted(src, i + 1, '"'); push('id', src.slice(i, j)); i = j; continue; }
      var word = stickyMatch(WORD, src, i);
      if (word) {
        var lower = word.toLowerCase();
        var cls = null;
        if (ch === '@' || ch === '#') cls = 'id';
        else {
          var k = i + word.length;
          while (k < n && (src[k] === ' ' || src[k] === '\t')) k++;
          if (src[k] === '(' && FUNCTIONS[lower]) cls = 'fn';
          else if (KEYWORDS[lower]) cls = 'kw';
        }
        push(cls, word); i += word.length; continue;
      }
      if (/[0-9]/.test(ch) || (ch === '.' && /[0-9]/.test(nx || ''))) {
        var num = stickyMatch(NUMBER, src, i);
        if (num) { push('num', num); i += num.length; continue; }
      }
      var op = stickyMatch(OPERATOR, src, i);
      if (op) { push('op', op); i += op.length; continue; }
      push(null, ch); i++;
    }
    return out;
  }

  function esc(s) {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  /* One escaped HTML string per source line; tokens that span lines are closed and reopened on each line. */
  function sqlLines(text) {
    var src = String(text == null ? '' : text).replace(/\r\n?/g, '\n');
    var lines = [''];
    tokenize(src).forEach(function (tok) {
      tok.v.split('\n').forEach(function (part, idx) {
        if (idx > 0) lines.push('');
        if (!part) return;
        lines[lines.length - 1] += tok.c ? '<span class="tok-' + tok.c + '">' + esc(part) + '</span>' : esc(part);
      });
    });
    return lines;
  }

  function sql(text) { return sqlLines(text).join('\n'); }

  DBM.highlight = { sql: sql, sqlLines: sqlLines, tokenize: tokenize };
})(globalThis.DBM = globalThis.DBM || {});
```

- [ ] **Step 4: Implement the view**

`plugins/db-migrate/engine/Dbm/wwwroot/js/views/sql.js` (the IIFE takes `globalThis.DBM` — identical to `window.DBM` in the browser — so node can load it to test the pure helpers; nothing touches the DOM at load time):

```js
/* views/sql.js — SQL phase: global pre/post, tasks in execution order, highlighted SQL with line anchors,
   in-place editing, per-task version diff, live validation and the script-pack download.
   Pure helpers are exposed as DBM.sqlView for node tests (no DOM access at load time). */
(function (DBM) {
  'use strict';

  var SECTIONS = ['pre', 'source', 'staging', 'merge', 'post'];
  var SECTION_TITLE = { pre: 'Task pre-load', source: 'Source query', staging: 'Staging table', merge: 'Merge into target', post: 'Task post-load' };
  var SECTION_SIDE = { pre: 'target', source: 'source', staging: 'target', merge: 'target', post: 'target' };

  /* ---------- pure helpers (mirrors Dbm.Core.SqlGen.TaskListing) ---------- */

  function norm(s) { return String(s == null ? '' : s).replace(/\r\n/g, '\n'); }

  function listing(task) {
    var out = [];
    var no = 0;
    function add(section, text) {
      if (!text) return;
      norm(text).split('\n').forEach(function (line) { out.push({ no: ++no, section: section, text: line }); });
    }
    (task.preSql || []).forEach(function (s) { add('pre', s); });
    add('source', task.sourceQuery);
    add('staging', task.stagingDdl);
    add('merge', task.mergeSql);
    (task.postSql || []).forEach(function (s) { add('post', s); });
    return out;
  }

  function listingText(task) { return listing(task).map(function (l) { return l.text; }).join('\n'); }

  function splitStatements(text) {
    return norm(text).split(/^[ \t]*GO[ \t]*$/im).map(function (s) { return s.trim(); }).filter(Boolean);
  }

  function joinStatements(list) { return (list || []).join('\nGO\n'); }

  function trimEnd(s) { return s.replace(/\s+$/, ''); }

  /* Patch ops (JSON pointers into the SQL artifact) for the fields the user changed. */
  function editOps(id, task, edits) {
    var ops = [];
    var base = '/tasks/' + id + '/';
    ['sourceQuery', 'stagingDdl', 'mergeSql'].forEach(function (f) {
      if (!Object.prototype.hasOwnProperty.call(edits, f)) return;
      var before = trimEnd(norm(task[f]));
      var after = trimEnd(norm(edits[f]));
      if (after === before) return;
      if (!after && f !== 'sourceQuery') { if (task[f] != null) ops.push({ op: 'remove', path: base + f }); return; }
      ops.push({ op: task[f] == null ? 'add' : 'replace', path: base + f, value: after });
    });
    ['preSql', 'postSql'].forEach(function (f) {
      if (!Object.prototype.hasOwnProperty.call(edits, f)) return;
      var after = splitStatements(edits[f]);
      var before = (task[f] || []).map(function (s) { return norm(s).trim(); });
      if (JSON.stringify(after) === JSON.stringify(before)) return;
      ops.push({ op: task[f] == null ? 'add' : 'replace', path: base + f, value: after });
    });
    return ops;
  }

  function findByTarget(plan, target) {
    if (!plan || !plan.tasks) return null;
    var ids = Object.keys(plan.tasks);
    for (var i = 0; i < ids.length; i++) if (plan.tasks[ids[i]].target === target) return plan.tasks[ids[i]];
    return null;
  }

  /* ---------- context helpers (ctx from app.js: artifact = the version being shown) ---------- */

  function payloadOf(ctx) {
    var a = ctx && ctx.artifact;
    return a && a.payload && a.payload.tasks && a.payload.order ? a.payload : null;
  }

  function versionOf(ctx) {
    if (typeof ctx.version === 'number') return ctx.version;
    if (ctx.artifact && typeof ctx.artifact.version === 'number') return ctx.artifact.version;
    return typeof ctx.latestVersion === 'number' ? ctx.latestVersion : null;
  }

  function versionsOf(ctx) { return Array.isArray(ctx.versions) ? ctx.versions : []; }

  function exported() { return !!globalThis.DBM_EXPORT; }

  /* app.js sets readOnly unless the phase awaits review and the current version is shown. */
  function canEdit(ctx) { return !ctx.readOnly && !exported(); }

  /* ---------- DOM helpers ---------- */

  function el(tag, attrs, kids) {
    var args = [tag, attrs || {}];
    (kids || []).forEach(function (k) {
      if (k === null || k === undefined || k === false) return;
      args.push(typeof k === 'number' ? String(k) : k);
    });
    return DBM.h.apply(null, args);
  }

  function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); }

  function list(cls, items) {
    return el('ul', { class: 'sql-msgs ' + cls }, items.map(function (m) { return el('li', {}, [m]); }));
  }

  function codeBlock(text) {
    var lines = norm(text).split('\n');
    var html = DBM.highlight.sqlLines(text);
    return el('pre', { class: 'code sql-code' }, lines.map(function (_, i) {
      return el('div', { class: 'line' }, [el('span', { class: 'ln' }, [String(i + 1)]), el('span', { html: html[i] || ' ' })]);
    }));
  }

  /* ---------- view state ---------- */

  var ui = { selected: null, editing: null, diff: null, report: null, busy: false, forVersion: undefined };
  var mounted = null;

  function rerender() { if (mounted) render(mounted.root, mounted.ctx); }

  function selectedId(plan) {
    if (ui.selected && plan.tasks[ui.selected]) return ui.selected;
    for (var i = 0; i < plan.order.length; i++) {
      var t = plan.tasks[plan.order[i]];
      if (t && t.errors && t.errors.length) return plan.order[i];
    }
    return plan.order[0] || null;
  }

  function select(id) { ui.selected = id; ui.editing = null; ui.diff = null; rerender(); }

  /* ---------- sections ---------- */

  function toolbar(ctx, plan) {
    if (exported() || !plan || !ctx.api) return null;
    var attrs = { class: 'btn' + (ui.busy ? ' is-loading' : ''), type: 'button', disabled: ui.busy, on: { click: function () { runValidate(ctx); } } };
    return el('div', { class: 'toolbar row' }, [
      el('button', attrs, [ui.busy ? 'Validating…' : 'Validate live']),
      el('a', { class: 'btn btn-ghost', href: ctx.api.url('/api/export/sqlpack'), download: true, title: 'Script pack of the current version' }, ['Download script pack']),
    ]);
  }

  function runValidate(ctx) {
    ui.busy = true;
    rerender();
    ctx.api.post('/api/sql/validate', {}).then(function (report) {
      ui.busy = false;
      ui.report = report;
      rerender();
      ctx.toast(report.ok ? 'Validation passed' : 'Validation found errors', report.ok ? 'ok' : 'err');
    }).catch(function (e) {
      ui.busy = false;
      rerender();
      ctx.toast((e && e.message) || 'Validation failed', 'err');
    });
  }

  function kpis(plan) {
    var tasks = plan.order.map(function (id) { return plan.tasks[id]; }).filter(Boolean);
    var custom = tasks.filter(function (t) { return t.custom; }).length;
    var warnings = (plan.warnings || []).length + tasks.reduce(function (n, t) { return n + (t.warnings || []).length; }, 0);
    var errors = (plan.errors || []).length + tasks.reduce(function (n, t) { return n + (t.errors || []).length; }, 0);
    return el('div', { class: 'grid-kpi' }, [
      DBM.components.kpi('Tasks', DBM.fmt.num(tasks.length), 'in execution order'),
      DBM.components.kpi('Custom SQL', DBM.fmt.num(custom), custom ? 'edited by agent or human' : 'all generated'),
      DBM.components.kpi('Warnings', DBM.fmt.num(warnings), 'review before approval'),
      DBM.components.kpi('Errors', DBM.fmt.num(errors), errors ? 'block approval' : 'none'),
    ]);
  }

  function reportCard(plan) {
    var r = ui.report;
    var rows = [];
    (r.globalErrors || []).forEach(function (e) { rows.push(el('li', { class: 'sql-count-err' }, ['plan: ' + e])); });
    plan.order.forEach(function (id) {
      var errs = (r.taskErrors && r.taskErrors[id]) || [];
      var warns = (r.taskWarnings && r.taskWarnings[id]) || [];
      if (!errs.length && !warns.length) return;
      rows.push(el('li', {}, [
        el('button', { class: 'chip sql-chip-btn', type: 'button', on: { click: function () { select(id); } } }, [id]),
        ' ',
        errs.length ? el('span', { class: 'sql-count-err' }, [errs.join(' • ')]) : null,
        errs.length && warns.length ? ' ' : null,
        warns.length ? el('span', { class: 'muted' }, [warns.join(' • ')]) : null,
      ]));
    });
    return el('section', { class: 'card sql-report' + (r.ok ? '' : ' is-bad'), 'aria-live': 'polite' }, [
      el('div', { class: 'card-h row' }, [
        el('h3', { class: 'h3' }, ['Live validation' + (r.version != null ? ' · v' + r.version : '')]),
        el('span', { class: 'badge ' + (r.ok ? 'st-approved' : 'st-failed') }, [r.ok ? 'ok' : 'errors']),
        el('span', { class: 'spacer' }),
        el('button', { class: 'btn btn-ghost btn-sm', type: 'button', on: { click: function () { ui.report = null; rerender(); } } }, ['Dismiss']),
      ]),
      el('div', { class: 'card-b' }, [rows.length ? el('ul', { class: 'sql-msgs' }, rows) : el('p', { class: 'muted' }, ['No errors or warnings.'])]),
    ]);
  }

  function globalCard(title, when, statements) {
    var body = statements && statements.length
      ? codeBlock(joinStatements(statements))
      : el('p', { class: 'muted small' }, ['None.']);
    return el('section', { class: 'card' }, [
      el('div', { class: 'card-h row' }, [el('h3', { class: 'h3' }, [title]), el('span', { class: 'small muted' }, [when])]),
      el('div', { class: 'card-b' }, [body]),
    ]);
  }

  function taskList(plan, activeId) {
    return el('nav', { class: 'sql-tasks', 'aria-label': 'Tasks in execution order' }, plan.order.map(function (id) {
      var t = plan.tasks[id];
      if (!t) return null;
      var errs = (t.errors || []).length;
      var warns = (t.warnings || []).length;
      var active = id === activeId;
      return el('button', {
        class: 'sql-task' + (active ? ' is-active' : ''), type: 'button', 'aria-current': active ? 'true' : null,
        on: { click: function () { select(id); } },
      }, [
        el('span', { class: 'sql-task-h' }, [el('span', { class: 'mono small' }, [id]), el('span', { class: 'ellipsis' }, [t.target])]),
        el('span', { class: 'sql-task-meta' }, [
          el('span', { class: 'tag' }, [t.mode === 'staging_merge' ? 'staging + merge' : 'direct']),
          t.custom ? el('span', { class: 'chip' }, ['custom']) : null,
          errs ? el('span', { class: 'pill sql-count-err' }, [errs + ' error' + (errs > 1 ? 's' : '')]) : null,
          warns ? el('span', { class: 'pill sql-count-warn' }, [warns + ' warning' + (warns > 1 ? 's' : '')]) : null,
          (t.dependsOn || []).length ? el('span', { class: 'small muted' }, ['after ' + t.dependsOn.join(', ')]) : null,
        ]),
      ]);
    }));
  }

  function taskDetail(ctx, plan, id, version) {
    var t = plan.tasks[id];
    var head = el('div', { class: 'card-h row row-wrap' }, [
      el('h2', { class: 'h2' }, [el('span', { class: 'mono' }, [id]), ' ', t.target]),
      el('span', { class: 'spacer' }),
      diffPicker(ctx, id, t, version),
      canEdit(ctx) && ui.editing !== id
        ? el('button', { class: 'btn btn-sm', type: 'button', on: { click: function () { ui.editing = id; ui.diff = null; rerender(); } } }, ['Edit SQL'])
        : null,
    ]);
    ctx.commentable(head, 'task:' + id, id + ' ' + t.target);

    var meta = el('div', { class: 'row-wrap sql-meta' }, [
      el('span', { class: 'tag' }, [t.mode === 'staging_merge' ? 'staging + merge' : 'direct']),
      t.identityInsert ? el('span', { class: 'tag' }, ['identity insert']) : null,
      el('span', { class: 'tag' }, [(t.keyColumns || []).length ? 'keys ' + t.keyColumns.join(', ') : 'no key · single transaction']),
      t.chunkSize ? el('span', { class: 'tag' }, ['chunk ' + DBM.fmt.num(t.chunkSize)]) : null,
      t.custom ? el('span', { class: 'chip' }, ['custom']) : null,
      (t.dependsOn || []).length ? el('span', { class: 'small muted' }, ['depends on']) : null,
    ].concat((t.dependsOn || []).map(function (dep) {
      return el('button', { class: 'chip sql-chip-btn', type: 'button', on: { click: function () { select(dep); } } }, [dep]);
    })));

    var body = [meta];
    if ((t.errors || []).length) body.push(list('sql-msgs-err', t.errors));
    if ((t.warnings || []).length) body.push(list('sql-msgs-warn', t.warnings));
    body.push(bindings(t));
    if (ui.editing === id) body.push(editor(ctx, id, t, version));
    else if (ui.diff && ui.diff.id === id) body.push(diffView(version));
    else body.push(sections(ctx, id, t));
    body.push(el('details', { class: 'sql-count' }, [
      el('summary', { class: 'small muted' }, ['Validation count query · runs on SOURCE']),
      codeBlock(t.countSql || ''),
    ]));
    return el('section', { class: 'card sql-detail' }, [head, el('div', { class: 'card-b stack' }, body)]);
  }

  function bindings(t) {
    var rows = (t.columns || []).map(function (c) {
      return el('tr', {}, [el('td', { class: 'mono' }, [c.source]), el('td', { class: 'muted' }, ['→']), el('td', { class: 'mono' }, [c.target])]);
    });
    return el('details', {}, [
      el('summary', { class: 'small muted' }, [(t.columns || []).length + ' column bindings (source alias → target column)']),
      el('table', { class: 'tbl tbl-compact' }, [el('tbody', {}, rows)]),
    ]);
  }

  function sections(ctx, id, t) {
    var all = listing(t);
    return el('div', { class: 'stack' }, SECTIONS.map(function (sec) {
      var lines = all.filter(function (l) { return l.section === sec; });
      if (!lines.length) return null;
      var html = DBM.highlight.sqlLines(lines.map(function (l) { return l.text; }).join('\n'));
      var pre = el('pre', { class: 'code sql-code' }, lines.map(function (l, i) {
        var row = el('div', { class: 'line', data: { line: String(l.no) } }, [
          el('span', { class: 'ln' }, [String(l.no)]),
          el('span', { html: html[i] || ' ' }),
        ]);
        ctx.commentable(row, 'sql:' + id + ':' + l.no, id + ' line ' + l.no);
        return row;
      }));
      var side = SECTION_SIDE[sec];
      return el('div', { class: 'sql-sec' }, [
        el('div', { class: 'sql-sec-h' }, [
          el('span', {}, [SECTION_TITLE[sec]]),
          el('span', { class: 'sql-side sql-side-' + (side === 'source' ? 'src' : 'tgt') }, ['runs on ' + side.toUpperCase()]),
        ]),
        pre,
      ]);
    }));
  }

  function diffPicker(ctx, id, t, version) {
    var others = versionsOf(ctx).filter(function (v) { return v.version !== version; });
    if (!others.length || exported() || !ctx.api) return null;
    var options = [el('option', { value: '' }, ['Compare with…'])].concat(others.slice().reverse().map(function (v) {
      return el('option', { value: String(v.version), selected: !!(ui.diff && ui.diff.with === v.version) }, ['v' + v.version + ' · ' + v.author]);
    }));
    return el('select', {
      class: 'select btn-sm', 'aria-label': 'Compare this task with another version',
      on: {
        change: function (e) {
          var v = parseInt(e.target.value, 10);
          if (isNaN(v)) { ui.diff = null; rerender(); return; }
          ctx.api.get('/api/artifact/sql/' + v).then(function (res) {
            var old = findByTarget(res.payload, t.target);
            ui.diff = { id: id, with: v, missing: !old, rows: DBM.diff.lines(old ? listingText(old) : '', listingText(t)) };
            ui.editing = null;
            rerender();
          }).catch(function (err) { ctx.toast((err && err.message) || 'Could not load v' + v, 'err'); });
        },
      },
    }, options);
  }

  function diffView(version) {
    var d = ui.diff;
    var changed = d.rows.some(function (r) { return r.op !== 'eq'; });
    return el('div', { class: 'stack' }, [
      el('div', { class: 'row' }, [
        el('span', { class: 'small muted' }, [d.missing ? 'Task not present in v' + d.with + '; everything is new.' : 'Changes from v' + d.with + ' to v' + version]),
        el('span', { class: 'spacer' }),
        el('button', { class: 'btn btn-ghost btn-sm', type: 'button', on: { click: function () { ui.diff = null; rerender(); } } }, ['Close diff']),
      ]),
      changed
        ? el('pre', { class: 'diff' }, d.rows.map(function (r) { return el('div', { class: 'diff-' + r.op }, [r.text]); }))
        : el('p', { class: 'muted' }, ['No differences in this task.']),
    ]);
  }

  function editor(ctx, id, t, version) {
    var fields = [['preSql', 'Task pre-load · TARGET · separate statements with a line containing only GO', joinStatements(t.preSql)],
      ['sourceQuery', 'Source query · runs on SOURCE · aliases = target columns, keys __k0…, no ORDER BY/TOP', t.sourceQuery || '']];
    if (t.mode === 'staging_merge' || t.stagingDdl || t.mergeSql) {
      fields.push(['stagingDdl', 'Staging table · TARGET · CREATE TABLE #stg (…)', t.stagingDdl || '']);
      fields.push(['mergeSql', 'Merge into target · TARGET · end MERGE with ;', t.mergeSql || '']);
    }
    fields.push(['postSql', 'Task post-load · TARGET · separate statements with a line containing only GO', joinStatements(t.postSql)]);

    var areas = {};
    var problems = el('div', { class: 'sql-edit-problems', 'aria-live': 'assertive' }, []);
    var submit = el('button', { class: 'btn btn-primary', type: 'submit' }, ['Save as new version']);

    function save() {
      var edits = {};
      Object.keys(areas).forEach(function (k) { edits[k] = areas[k].value; });
      var ops = editOps(id, t, edits);
      if (!ops.length) { ctx.toast('Nothing changed', 'info'); return; }
      submit.disabled = true;
      clear(problems);
      ctx.api.post('/api/edit/sql', { phase: 'sql', baseVersion: version, ops: ops, responses: [], summary: 'Edited ' + id + ' (' + t.target + ') in the UI' })
        .then(function (res) {
          if (res && res.ok === false) { show((res.errors || []).concat(res.warnings || [])); return; }
          ui.editing = null;
          ctx.toast('Saved as v' + (res && res.version), 'ok');
          ctx.refresh();
        })
        .catch(function (e) { show(e && e.details && e.details.length ? e.details : [(e && e.message) || 'Save failed']); });
    }

    function show(messages) {
      submit.disabled = false;
      clear(problems);
      problems.appendChild(list('sql-msgs-err', messages));
    }

    var controls = fields.map(function (f) {
      var lines = norm(f[2]).split('\n').length;
      var ta = el('textarea', {
        class: 'textarea sql-edit', spellcheck: 'false', rows: String(Math.min(24, Math.max(4, lines + 1))),
        on: { keydown: function (e) { if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S')) { e.preventDefault(); save(); } } },
      }, []);
      ta.value = f[2];
      areas[f[0]] = ta;
      return el('label', { class: 'stack sql-edit-field' }, [el('span', { class: 'small muted' }, [f[1]]), ta]);
    });

    return el('form', { class: 'stack sql-editor', on: { submit: function (e) { e.preventDefault(); save(); } } }, controls.concat([
      problems,
      el('div', { class: 'row' }, [
        submit,
        el('button', { class: 'btn btn-ghost', type: 'button', on: { click: function () { ui.editing = null; rerender(); } } }, ['Cancel']),
        el('span', { class: 'small muted' }, ['Ctrl+S saves · the server validates live before storing']),
      ]),
    ]));
  }

  /* ---------- view ---------- */

  function render(root, ctx) {
    mounted = { root: root, ctx: ctx };
    var plan = payloadOf(ctx);
    var version = versionOf(ctx);
    if (ui.forVersion !== version) { ui.editing = null; ui.diff = null; ui.report = null; ui.forVersion = version; }
    clear(root);

    var page = el('div', { class: 'page stack sql-page' }, [
      el('div', { class: 'page-h row row-wrap' }, [el('h1', { class: 'h1' }, ['Migration SQL']), el('span', { class: 'spacer' }), toolbar(ctx, plan)]),
    ]);
    root.appendChild(page);
    if (DBM.components.reviewBar) page.appendChild(DBM.components.reviewBar(ctx));
    if (!plan) {
      page.appendChild(DBM.components.emptyState('No SQL plan yet', 'The plan is generated as soon as the mapping is approved.'));
      return;
    }

    page.appendChild(kpis(plan));
    if (ui.report) page.appendChild(reportCard(plan));
    if ((plan.errors || []).length || (plan.warnings || []).length) {
      page.appendChild(el('section', { class: 'card' }, [
        el('div', { class: 'card-h' }, [el('h3', { class: 'h3' }, ['Plan notes'])]),
        el('div', { class: 'card-b' }, [
          (plan.errors || []).length ? list('sql-msgs-err', plan.errors) : null,
          (plan.warnings || []).length ? list('sql-msgs-warn', plan.warnings) : null,
        ]),
      ]));
    }
    page.appendChild(globalCard('Global pre-load', 'runs on TARGET before the first task', plan.preSql));
    var id = selectedId(plan);
    page.appendChild(el('div', { class: 'sql-layout' }, [
      taskList(plan, id),
      id ? taskDetail(ctx, plan, id, version) : DBM.components.emptyState('No tasks', 'Every target table is skipped in the mapping.'),
    ]));
    page.appendChild(globalCard('Global post-load', 'runs on TARGET after the last task', plan.postSql));
  }

  DBM.sqlView = { listing: listing, listingText: listingText, splitStatements: splitStatements, joinStatements: joinStatements, editOps: editOps };
  DBM.views = DBM.views || {};
  DBM.views.sql = { title: 'SQL', render: render };
})(globalThis.DBM = globalThis.DBM || {});
```

- [ ] **Step 5: Run the JS tests to verify they pass**

Run: `node --test plugins/db-migrate/engine/Dbm.Tests/js/highlight.test.cjs plugins/db-migrate/engine/Dbm.Tests/js/sql-view.test.cjs`
Expected: `ℹ tests 18`, `ℹ pass 18`, `ℹ fail 0`.

Run the whole JS suite: `node --test "plugins/db-migrate/engine/Dbm.Tests/js/*.test.cjs"` (Node 24 needs the quoted glob form) — Expected: `ℹ fail 0`.

- [ ] **Step 6: Add the validate endpoint (test first) and register it**

`plugins/db-migrate/engine/Dbm.Tests/Unit/Web/SqlEndpointsTests.cs`:

```csharp
using System.Net;
using Dbm.Core;
using Dbm.Tests.Support;

namespace Dbm.Tests.Unit.Web;

public sealed class SqlEndpointsTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Validate_without_a_version_is_a_conflict()
    {
        _workspace.OpenServices();
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));
        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/sql/validate", new { });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("no_version", (string?)body!["error"]);
    }

    [Fact]
    public async Task Validate_without_connections_is_not_ready()
    {
        SqlExportsTests.WithPlan(_workspace);
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));
        var (status, body) = await server.SendAsync(HttpMethod.Post, "/api/sql/validate", new { task = "T01" });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("not_ready", (string?)body!["error"]);
    }

    [Fact]
    public async Task Sql_screen_exports_as_standalone_html()
    {
        SqlExportsTests.WithPlan(_workspace);
        await using var server = await WebTestServer.StartAsync(_workspace.Ws, ws => DbmServices.Open(ws));

        using var response = await server.Client.GetAsync("/api/export/sql");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("window.DBM_EXPORT", html);
        Assert.Contains("DBM.views.sql = ", html);
        Assert.Contains("DBM.highlight = ", html);
        Assert.Contains("FK_Customers_PrimaryAddress", html);
    }
}
```

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Web.SqlEndpointsTests"`
Expected: the two `Validate_*` tests FAIL (the route does not exist yet, so the status is not `Conflict`); `Sql_screen_exports_as_standalone_html` already passes (T4.4 registered the `sql` export and Step 4 created the view).

`plugins/db-migrate/engine/Dbm/Web/Endpoints/SqlEndpoints.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Dbm.Core;
using Dbm.Core.SqlGen;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dbm.Web.Endpoints;

/// <summary>POST /api/sql/validate {task?} → live ValidationReport for the current sql version (nothing is stored).</summary>
public static class SqlEndpoints
{
    public sealed record ValidateRequest(string? Task);

    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        app.MapPost("/api/sql/validate", async (HttpContext http, CancellationToken ct) =>
        {
            ValidateRequest? body = null;
            using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
            {
                var text = await reader.ReadToEndAsync(ct);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try { body = Json.Deserialize<ValidateRequest>(text); }
                    catch (JsonException ex) { return ApiResults.Error(StatusCodes.Status400BadRequest, "bad_request", $"Invalid JSON body: {ex.Message}"); }
                }
            }

            var s = state.Services;
            var row = SqlPlanSource.CurrentRow(s);
            if (row is null) return ApiResults.Error(StatusCodes.Status409Conflict, "no_version", "The sql phase has no version yet.");
            if (!SqlPlanSource.CanValidate(s))
                return ApiResults.Error(StatusCodes.Status409Conflict, "not_ready", "Both connections and the target catalog are needed to validate.");

            var plan = Json.Deserialize<SqlPlanPayload>(row.PayloadJson);
            var report = await SqlPlanSource.ValidateLiveAsync(s, plan, string.IsNullOrWhiteSpace(body?.Task) ? null : body!.Task, ct);
            return ApiResults.Json(new
            {
                version = row.Version,
                ok = report.Ok,
                taskErrors = report.TaskErrors,
                taskWarnings = report.TaskWarnings,
                globalErrors = report.GlobalErrors,
            });
        });
    }
}
```

`plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs` (T1.7) — inside `MapAll`, replace the placeholder comment line

```csharp
        // T4.5: SqlEndpoints.Map(app, state);
```

with

```csharp
        SqlEndpoints.Map(app, state);       // T4.5
```

Run: `dotnet test plugins/db-migrate/engine/Dbm.Tests --filter "FullyQualifiedName~Dbm.Tests.Unit.Web.SqlEndpointsTests"`
Expected: `Passed!  - Failed: 0, Passed: 3`.

- [ ] **Step 7: Wire the scripts and styles**

`plugins/db-migrate/engine/Dbm/wwwroot/index.html` (T1.9) — directly after the line `  <script src="js/lib/graph.js"></script>` add:

```html
  <script src="js/lib/highlight.js"></script>
```

and directly after the line `  <script src="js/views/mapping.js"></script>` add:

```html
  <script src="js/views/sql.js"></script>
```

This keeps the C9 load order (`lib/*` → `api.js` → components → views → `app.js`); leave the two marker comments in place.

`plugins/db-migrate/engine/Dbm/wwwroot/css/app.css` (T1.9) — append at the very end of the file (after the T3.5 mapping block):

```css

/* ---- SQL view (T4.5, js/views/sql.js) ---- */
.sql-layout { display: grid; grid-template-columns: minmax(220px, 300px) minmax(0, 1fr); gap: 16px; align-items: start; }
.sql-tasks { display: flex; flex-direction: column; gap: 4px; position: sticky; top: 8px; max-height: calc(100vh - 16px); overflow: auto; }
.sql-task {
  display: grid; gap: 4px; width: 100%; padding: 8px 12px; text-align: left; font: inherit; color: var(--text);
  background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); cursor: pointer;
}
.sql-task:hover { background: var(--surface-2); }
.sql-task.is-active { border-color: var(--accent); box-shadow: inset 3px 0 0 var(--accent); }
.sql-task:focus-visible, .sql-chip-btn:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.sql-task-h { display: flex; gap: 8px; align-items: baseline; min-width: 0; }
.sql-task-meta, .sql-meta { display: flex; flex-wrap: wrap; gap: 4px; align-items: center; }
.sql-detail { min-width: 0; }
.sql-count-err { color: var(--err); }
.sql-count-warn { color: var(--warn); }
.sql-sec-h {
  display: flex; gap: 8px; align-items: baseline; margin: 0 0 4px;
  font-size: 12px; letter-spacing: .04em; text-transform: uppercase; color: var(--text-muted);
}
.sql-side { font-weight: 600; }
.sql-side-src { color: var(--info); }
.sql-side-tgt { color: var(--accent); }
.sql-code { max-height: 480px; overflow: auto; margin: 0; }
.sql-msgs { margin: 0; padding-left: 20px; }
.sql-msgs li { margin: 2px 0; overflow-wrap: anywhere; }
.sql-msgs-err li { color: var(--err); }
.sql-msgs-warn li::marker { color: var(--warn); }
.sql-report { border-left: 3px solid var(--ok); }
.sql-report.is-bad { border-left-color: var(--err); }
.sql-chip-btn { cursor: pointer; font: inherit; border: 0; }
.sql-edit {
  width: 100%; min-height: 96px; font-family: var(--font-mono); font-size: 13px; line-height: 1.5;
  tab-size: 4; resize: vertical; white-space: pre; overflow-wrap: normal; overflow-x: auto;
}
.sql-edit-field { gap: 4px; }
.sql-detail summary { cursor: pointer; }
@media (max-width: 760px) {
  .sql-layout { grid-template-columns: minmax(0, 1fr); }
  .sql-tasks { position: static; max-height: none; flex-direction: row; overflow-x: auto; padding-bottom: 4px; }
  .sql-task { min-width: 220px; }
}
```

Colours come only from the C9 custom properties, so light and dark themes need no extra rules; the `.tok-*`, `.code .ln`, `.code .line` and `.diff-*` styles come from T1.9.

Run: `dotnet build plugins/db-migrate/engine/Dbm` — Expected: `Build succeeded.` (wwwroot is copied to the output, so the export test and the UI see the new files).

- [ ] **Step 8: Manual verification**

Start a demo project (`dbm demo --server "Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true"`, approve analysis and mapping, wait for the sql phase to reach `awaiting_review`), open `dbm ui` and go to **SQL**. Check, in the light theme, the dark theme (toggle and OS preference) and at ~400 px width (browser dev tools):

1. KPI row shows 6 tasks; task list order is T01 app.AuditEvents … T06 app.OrderLines; T04 shows "after T02"; T05 shows the warning pill and the trigger warning in its detail.
2. Global pre-load shows `ALTER TABLE [app].[Customers] NOCHECK CONSTRAINT [FK_Customers_PrimaryAddress];`, post-load the `WITH CHECK CHECK` statement; both highlighted (keywords, bracketed identifiers).
3. T05 detail: "Source query · RUNS ON SOURCE" block with line numbers 1–9, `st.[STATUS_CD]` identifier-coloured; hovering any line shows the comment button; commenting line 5 creates a draft with anchor `sql:T05:5`; commenting the header creates `task:T05`.
4. **Validate live** shows "Validating…", then the report card with `ok` and the `Comment: varchar(500) -> nvarchar(200)` warning under T05; the T05 chip in the report selects the task; Dismiss removes the card.
5. **Download script pack** downloads `<project>-sql-v<n>.zip` (current version) containing 9 files; a copy appears under `.dbmigrate/exports/`.
6. **Edit SQL** on T04: textareas are monospace, horizontally scrollable, Ctrl+S saves; add `WHERE (s.[CITY] <> '')` to the source query → a toast "Saved as v<n+1>" and the task gets the `custom` chip; introduce `s.[NOPE]` → the save is rejected and the error list shows `T04: sourceQuery: Invalid column name 'NOPE'.`
7. **Compare with…** on T04 → choose the previous version → a line diff with the added `WHERE` line in `.diff-add` colours; "Close diff" returns to the code.
8. At ~400 px: the task list is a horizontal strip, code blocks scroll horizontally inside their card, no page-level horizontal scrollbar; keyboard: Tab reaches task buttons (visible focus ring), Enter selects.
9. Picking an older version in the review bar, or an approved phase: no Edit button (read-only); Validate and Download still work. `GET /api/export/sql?t=<token>` opens a standalone read-only copy without toolbar or diff picker.

- [ ] **Step 9: Commit**

```bash
git add plugins/db-migrate/engine/Dbm/wwwroot/js/lib/highlight.js plugins/db-migrate/engine/Dbm/wwwroot/js/views/sql.js plugins/db-migrate/engine/Dbm/Web/Endpoints/SqlEndpoints.cs plugins/db-migrate/engine/Dbm/Web/Endpoints/EndpointRegistry.cs plugins/db-migrate/engine/Dbm/wwwroot/index.html plugins/db-migrate/engine/Dbm/wwwroot/css/app.css plugins/db-migrate/engine/Dbm.Tests/js/highlight.test.cjs plugins/db-migrate/engine/Dbm.Tests/js/sql-view.test.cjs plugins/db-migrate/engine/Dbm.Tests/Unit/Web/SqlEndpointsTests.cs
git commit -F - <<'EOF'
feat(ui): SQL review screen with highlighter, line comments, edit, diff and validation

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01VKdXFFrbE43SLdkn44Ag49
EOF
```

---

## Contract notes

**M4 public API for M5/M6 (stable):** `SqlPlanPayload`, `TaskPlan`, `ColumnBinding`, `ValidationReport` (C13), `SqlGenerator.Generate/CountSql/MappingHash`, `SqlValidator.ValidateAsync/CheckShape/Apply`, `ScriptPack.BuildZip/BuildFiles`, `SqlQuote` (C3), `TopoSort`, `TaskListing`, `SqlPlanSource`, `SqlExports.Pack/Html`. The only change since the first draft of this plan: the script-pack route is no longer a separate `SqlPackEndpoint` class — it is the T2.8 export kind `sqlpack` registered by `SqlExports.Map` (same URL `GET /api/export/sqlpack`, current version only; `?version=` is not supported).

**Additions to C13 (members added, nothing renamed):**
- `SqlPlanPayload.Errors` (global validation errors) and the non-serialised methods `ErrorCount()` / `WarningCount()`.
- `TaskPlan.MappingHash` — lower-hex SHA-256 of `Json.Serialize(tableMap)`; carry-over matches on `Custom && Target && MappingHash`.
- `TopoSort.Sort(nodes, edges, Func<string,string,bool>? preferBreak)` overload; the 2-argument contract method calls it with `null`. `Order` is Kahn **by dependency level** with ordinal order inside a level (this is what puts `app.Products` before `app.Addresses` in the sample).
- `SqlValidator.CheckShape`, `Apply(plan, report, full)`, `DescribeAsync`, `ParseCheckAsync`, `WarningPrefix = "validate: "`, `ResultColumn`; `SqlGenerator.CountSql`, `MappingHash`, `LobChunkSize`, `NoKeyWarning`, `ReviewMergeWarning`, `CarriedWarning`; `ScriptPack.BuildFiles`.
- New M4 files not in the overview file map: `SqlGen/SqlTypeText.cs`, `SqlGen/SqlPlanSource.cs`, `SqlGen/TaskListing.cs`, `Web/Endpoints/SqlExports.cs`.

**Facts other milestones must respect:**
- `TaskPlan.CountSql` is `"SELECT COUNT_BIG(*) FROM (\n" + SourceQuery + "\n) AS q"` (newlines keep a trailing `--` comment harmless). M5 should run `CountSql` as stored; `SqlModule.Validate` recomputes it whenever a SourceQuery changes.
- **Never put `SET PARSEONLY ON; …; SET PARSEONLY OFF;` in one batch** — verified on LocalDB that the middle statement executes. Use three batches (as `SqlValidator.ParseCheckAsync` does) on a non-pooled connection.
- Line anchors `sql:<taskId>:<line>` use the `TaskListing` numbering (pre → source → staging → merge → post, continuous, 1-based). `views/sql.js` (`DBM.sqlView.listing`) and `SqlModule` packets use the same rules; anything else that renders task SQL with line anchors must too.
- For M5: `KeyColumns` are **result-column aliases** (`__k0`…), not target columns — they drive keyset chunking and must not be bulk-copied; `Columns` (alias → target column) is the SqlBulkCopy column mapping; `IdentityInsert` → `KeepIdentity`; `ChunkSize` overrides the run's chunk size; `staging_merge` = run `StagingDdl` on the chunk's target connection, bulk-copy into `#stg`, then `MergeSql`, same transaction; task `PreSql` before, `PostSql` after the task; global `PreSql`/`PostSql` around the whole run; `DependsOn` never contains a cycle edge.
- Validator warnings stored in the artifact are prefixed `validate: ` so a re-validation replaces them; generator warnings have no prefix.
- Global `PreSql`/`PostSql` are always regenerated by `sqlgen`; only task-level custom SQL carries over (when the task's TableMap is unchanged).
- `SqlModule.Validate` normalises the payload `JsonNode` in place; T1.5's `WorkflowEngine.Apply` stores exactly that node after `Validate` returns (confirmed in the M1 code and pinned by `SqlModuleTests.Human_edit_stores_the_custom_flag_set_by_validate`).
- CLI: `dbm sql validate` has an extra `--patch <file>` option (applies the patch in memory before validating) and exits 1 when the report is not ok; `dbm sql gen` on a drafting phase sets it back to `running` before queueing. The sql-engineer playbook depends on both.
- Exports: `sqlpack` (zip) and `sql` (standalone HTML) are T2.8 export kinds registered by `SqlExports.Map`; M6's `dbm export sqlpack` / `dbm export sql` should call `SqlExports.Pack(services)` / `SqlExports.Html(services, WebExport.LocateAssets())` (or `ScriptPack.BuildZip`) so names stay `<project>-sql-v<n>.zip` / `sql-v<n>.html`.
- `ServerMeta` (never the connection string) goes into the SQL packet as `data.servers`.
- Upstream plan edits needed: none. M1–M3 were used as published (registries below their `// milestone registrations below` markers, `EndpointRegistry` placeholder line, `index.html` script lines, `ExportEndpoints.Register`, `WebExport.LibOrder` already containing `highlight.js`).

## Self-review

- **Spec coverage:** §5.3 SQL artifact (ordered tasks, direct/staging_merge, source query, keys, column map, identity insert, pre/post, validation SQL) → T4.1/T4.2; §3.2 review loop for SQL (script draft, agent, anchored feedback on task/line, versions, diff, approval blockers) → T4.4/T4.5; §6 `dbm sql gen` / `dbm sql validate [--task]` with `SET PARSEONLY`, `sp_describe_first_result_set` and target-type compatibility → T4.3; §8 SQL screen (execution order, highlighted code, pre/post/validation scripts, line comments, version diff, script-pack download) → T4.5; §9 FK cycles NOCHECK/WITH CHECK → T4.2; §4.2 sql-engineer (packet, patch, must pass validation) → T4.4; §11 unit tests for topological sort/cycles and SQL templating, integration "SQL validate" on the sample pair → T4.1–T4.3.
- **Team-lead checklist:** order Products/Customers before Addresses, Orders after Customers+Addresses, OrderLines after Orders+Products ✓; exact SourceQuery for Addresses and Orders (merge `From` with `st`) ✓; composite key aliases for OrderLines ✓; exact Pre/Post for `FK_Customers_PrimaryAddress` ✓; IdentityInsert flags ✓; AuditEvents no keys + warning ✓; carry-over kept and invalidated ✓; validator Ok with the real `TypeCompat` / unknown column / `Comment` risk ✓; module packet shape, Custom marking (incl. through `HumanEdit`), blockers ✓; script pack entries and content, served through `ExportEndpoints.Register` ✓; highlighter escaping (`<script>` in a literal), comments, brackets ✓.
- **Deliberate deviations:** three-batch PARSEONLY instead of the single batch (single batch executes the statement); `NeedsAgent` ignores `staging_merge` tasks already `Custom`; `CountSql` contains newlines; the script pack always exports the current version.
- **Placeholder scan:** no TBD/TODO; every code block is complete; the only `…` characters are UI strings and prose.
- **Verification:** this plan was executed task by task on top of the M0–M3 integration repo (byte-exact from their plan text); every code block in this file is byte-identical to the files that then passed: full unit suite 672 (597 upstream + 75 M4), full LocalDB integration suite 45 (35 + 10 M4, incl. `Generated_sample_plan_validates_ok` with the real `TypeCompat`/`SampleCatalogs`/`SampleMappings` and `SqlCommandsIntegrationTests`), full node suite 54 (36 + 18 M4). Not automated: the rendered screen — covered by the manual checklist in T4.5 Step 8.
