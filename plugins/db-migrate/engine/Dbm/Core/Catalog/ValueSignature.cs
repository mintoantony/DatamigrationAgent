using System.Text;
using System.Text.RegularExpressions;

namespace Dbm.Core.Catalog;

/// <summary>Value shapes used by the profiler and the auto-mapper's profile score.</summary>
public static class ValueSignature
{
    private const RegexOptions Rx = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex Email = new(@"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$", Rx);
    private static readonly Regex Url = new(@"^((https?|ftp)://|www\.)\S+$", Rx | RegexOptions.IgnoreCase);
    private static readonly Regex DateTimeValue = new(
        @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:?\d{2})?$|^\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}[ T]\d{1,2}:\d{2}(:\d{2})?(\s?[AaPp][Mm])?$", Rx);
    private static readonly Regex DateValue = new(@"^\d{4}-\d{2}-\d{2}$|^\d{4}/\d{2}/\d{2}$|^\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}$", Rx);
    private static readonly Regex Postal = new(
        @"^\d{5}(-\d{4})?$|^[A-Z]{1,2}\d[A-Z\d]? ?\d[A-Z]{2}$|^[A-Z]\d[A-Z] ?\d[A-Z]\d$|^\d{4} ?[A-Z]{2}$", Rx);
    private static readonly Regex Integer = new(@"^[-+]?\d{1,18}$", Rx);
    private static readonly Regex Decimal = new(@"^[-+]?\d{1,18}[.,]\d+$", Rx);
    private static readonly Regex Phone = new(@"^\+?[\d\s\-().]{7,24}$", Rx);
    private static readonly Regex Country = new(@"^[A-Z]{2}$", Rx);

    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "y", "n", "yes", "no", "t", "f", "true", "false", "0", "1",
    };

    // Order matters: the first class that matches >= 80 % of the values wins.
    private static readonly (string Name, Func<string, bool> Test)[] Classes =
    {
        ("guid", v => Guid.TryParseExact(v, "D", out _) || Guid.TryParseExact(v, "B", out _)),
        ("email", Email.IsMatch),
        ("url", Url.IsMatch),
        ("datetime", DateTimeValue.IsMatch),
        ("date", DateValue.IsMatch),
        ("flag", Flags.Contains),
        ("postal_code", Postal.IsMatch),
        ("integer", Integer.IsMatch),
        ("decimal", Decimal.IsMatch),
        ("phone", v => Phone.IsMatch(v) && v.Count(char.IsDigit) >= 7),
        ("country_code", Country.IsMatch),
    };

    /// <summary>Letters -> 'a'/'A', digits -> '9', other characters kept; runs of the same symbol collapse to one.</summary>
    public static string Pattern(string value)
    {
        var sb = new StringBuilder(value.Length);
        var last = '\0';
        foreach (var ch in value)
        {
            var symbol = char.IsDigit(ch) ? '9' : char.IsLetter(ch) ? (char.IsUpper(ch) ? 'A' : 'a') : ch;
            if (symbol == last) continue;
            sb.Append(symbol);
            last = symbol;
        }
        return sb.ToString();
    }

    /// <summary>
    /// email | phone | url | guid | date | datetime | integer | decimal | postal_code | flag | country_code when at least 80 %
    /// of the non-blank values match; "text" otherwise; null when there are no non-blank values.
    /// </summary>
    public static string? Classify(IReadOnlyCollection<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        if (list.Count == 0) return null;
        foreach (var (name, test) in Classes)
        {
            var hits = list.Count(test);
            if (hits * 5 >= list.Count * 4) return name;
        }
        return "text";
    }
}
