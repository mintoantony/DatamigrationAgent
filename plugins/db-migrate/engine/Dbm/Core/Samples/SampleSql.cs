using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Dbm.Core.Samples;

/// <summary>The LegacyShop → ShopV2 sample scripts (samples/legacyshop-to-shopv2/*.sql), embedded into Dbm.dll.</summary>
public static class SampleSql
{
    private const string ResourcePrefix = "samples/legacyshop-to-shopv2/";

    /// <summary>The last statement of seed.sql; run on its own when the source is created without data.</summary>
    public const string UntrustedForeignKey =
        "ALTER TABLE dbo.ORD_HDR WITH NOCHECK ADD CONSTRAINT FK_ORD_CUST FOREIGN KEY (CUST_ID) REFERENCES dbo.CUST (CUST_ID);";

    private static readonly Regex GoLine = new(@"^\s*GO\s*(--.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string LegacyShopSchema => Load("legacyshop.sql");

    public static string ShopV2Schema => Load("shopv2.sql");

    /// <summary>seed.sql with every <c>$(scale)</c> token replaced by <paramref name="scale"/>.</summary>
    public static string Seed(int scale)
    {
        if (scale < 1) throw new ArgumentOutOfRangeException(nameof(scale), scale, "scale must be >= 1");
        return Load("seed.sql").Replace("$(scale)", scale.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>Splits a script on lines that contain only <c>GO</c> (case-insensitive, optional trailing comment). Empty batches are dropped.</summary>
    public static IReadOnlyList<string> SplitBatches(string sql)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (var line in sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (GoLine.IsMatch(line))
            {
                Flush();
                continue;
            }
            current.Append(line).Append('\n');
        }
        Flush();
        return batches;

        void Flush()
        {
            var batch = current.ToString().Trim();
            if (batch.Length > 0) batches.Add(batch);
            current.Clear();
        }
    }

    /// <summary>Runs every batch of <paramref name="script"/> on an open connection (10 minute timeout per batch).</summary>
    public static async Task ExecuteAsync(SqlConnection conn, string script, CancellationToken ct)
    {
        foreach (var batch in SplitBatches(script))
        {
            await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static string Load(string fileName)
    {
        using var stream = typeof(SampleSql).Assembly.GetManifestResourceStream(ResourcePrefix + fileName)
            ?? throw new InvalidOperationException($"Embedded sample script '{ResourcePrefix}{fileName}' not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
