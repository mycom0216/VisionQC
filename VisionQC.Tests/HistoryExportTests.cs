using System.Globalization;
using VisionQC.Core;

namespace VisionQC.Tests;

public class HistoryExportTests
{
    [Fact]
    public void Csv_preserves_quotes_commas_newlines_and_uses_invariant_numbers()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            using var writer = new StringWriter();
            HistoryCsv.Write(writer, new[] { Row("C:/검사/a,\"b.png", "한글\r\n오류") });
            var csv = writer.ToString();
            Assert.Contains("\"0.75\"", csv);
            Assert.Contains("\"C:/검사/a,\"\"b.png\"", csv);
            Assert.Contains("\"한글\r\n오류\"", csv);
            Assert.Contains("original_verdict", csv);
            Assert.EndsWith("\r\n", csv);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData("=HYPERLINK(\"bad\")")]
    [InlineData(" +SUM(1,2)")]
    [InlineData("@SUM(1)")]
    [InlineData("-cmd")]
    [InlineData("\tformula")]
    public void Csv_neutralizes_spreadsheet_formula_prefixes(string input)
    {
        using var writer = new StringWriter();
        HistoryCsv.Write(writer, new[] { Row(input, input) });
        Assert.Contains("\"'" + input.Replace("\"", "\"\""), writer.ToString());
    }

    [Fact]
    public void Empty_csv_still_has_column_header()
    {
        using var writer = new StringWriter();
        HistoryCsv.Write(writer, Array.Empty<InspectionResult>());
        Assert.Single(writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Local_day_includes_end_and_converts_to_utc()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("KST-test", TimeSpan.FromHours(9), "KST", "KST");
        var (from, to) = HistoryPeriod.ToUtc(new DateTime(2026, 11, 17), new DateTime(2026, 11, 17), zone);
        Assert.Equal(new DateTime(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(from!.Value.AddDays(1).AddTicks(-1), to);
    }

    [Fact]
    public void Reversed_dates_are_rejected_and_open_range_is_allowed()
    {
        Assert.Throws<ArgumentException>(() => HistoryPeriod.ToUtc(new DateTime(2026, 11, 18), new DateTime(2026, 11, 17), TimeZoneInfo.Utc));
        Assert.Equal((null, null), HistoryPeriod.ToUtc(null, null, TimeZoneInfo.Utc));
    }

    private static InspectionResult Row(string path, string? error) => new()
    {
        Id = 1, JobId = 2, ImagePath = path, Verdict = Verdict.NG, Score = 0.75f,
        ElapsedMs = 21, ErrorMessage = error, InspectedAtUtc = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc)
    };
}
