using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Dbm.Core;
using Dbm.Core.SqlGen;
using Dbm.Core.State;
using Microsoft.AspNetCore.Routing;

namespace Dbm.Web.Endpoints;

/// <summary>Registers the SQL exports with ExportEndpoints (T2.8), for the sql phase's current version:
/// <c>sqlpack</c> → the DBA script pack (zip), <c>sql</c> → the standalone read-only SQL screen (HTML).
/// <para>The <c>sql</c> kind needs <c>wwwroot/js/views/sql.js</c>, which T4.5 creates: until then it answers 404
/// <c>export_unavailable</c> ("View script js/views/sql.js not found.") BY DESIGN — do not stub the view.</para></summary>
public static class SqlExports
{
    public static void Map(IEndpointRouteBuilder app, WebState state)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(state);
        ExportEndpoints.Register("sqlpack", (s, _, _) => Task.FromResult(Pack(s.Services)));
        ExportEndpoints.Register("sql", (s, assets, _) => Task.FromResult(Html(s.Services, assets)));
    }

    /// <summary>"&lt;project&gt;-sql-v&lt;n&gt;.zip"; ExportException when the phase has no version yet.</summary>
    public static ExportFile Pack(DbmServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var row = Current(services);
        var project = services.Project.Get().Name;
        var bytes = ScriptPack.BuildZip(Json.Deserialize<SqlPlanPayload>(row.PayloadJson), project);
        return new ExportFile(FormattableString.Invariant($"{Safe(project)}-sql-v{row.Version}.zip"), "application/zip", bytes);
    }

    /// <summary>Standalone HTML of the SQL screen: payload {project, exportedAt, artifact:{version,author,summary,createdAt,payload}}.</summary>
    public static ExportFile Html(DbmServices services, IWebAssets assets)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assets);
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
                ["createdAt"] = row.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                ["payload"] = JsonNode.Parse(row.PayloadJson),
            },
        };
        var html = WebExport.BuildHtml(assets, "sql", FormattableString.Invariant($"{project} — SQL v{row.Version}"), data);
        return new ExportFile(FormattableString.Invariant($"sql-v{row.Version}.html"), "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
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
