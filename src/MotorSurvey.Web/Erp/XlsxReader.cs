using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace MotorSurvey.Web.Erp;

/// <summary>Minimal .xlsx reader (no packages): returns a sheet as rows of header -> text value.</summary>
public static class XlsxReader
{
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<Dictionary<string, string>> ReadSheet(string path, string sheetName)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); // works while Excel has it open
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        XDocument Load(string name) => XDocument.Load(zip.GetEntry(name)?.Open() ?? throw new InvalidDataException($"Missing {name}"));

        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } ss)
            shared = XDocument.Load(ss.Open()).Root!.Elements(M + "si").Select(si => string.Concat(si.Descendants(M + "t").Select(t => t.Value))).ToList();

        var wb = Load("xl/workbook.xml");
        var sheet = wb.Root!.Element(M + "sheets")!.Elements(M + "sheet")
            .FirstOrDefault(s => string.Equals((string?)s.Attribute("name"), sheetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Sheet '{sheetName}' not found");
        var rid = (string)sheet.Attribute(R + "id")!;
        var target = (string)Load("xl/_rels/workbook.xml.rels").Root!.Elements(P + "Relationship").First(r => (string?)r.Attribute("Id") == rid).Attribute("Target")!;
        var entry = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

        var rows = Load(entry).Root!.Element(M + "sheetData")!.Elements(M + "row")
            .Select(row => row.Elements(M + "c").ToDictionary(c => ColIndex((string)c.Attribute("r")!), c => CellText(c, shared)))
            .ToList();
        if (rows.Count == 0) return [];
        var header = rows[0];
        return rows.Skip(1)
            .Where(r => r.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
            .Select(r => header.ToDictionary(h => h.Value.Trim(), h => r.TryGetValue(h.Key, out var v) ? v.Trim() : "", StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static string CellText(XElement c, List<string> shared)
    {
        var t = (string?)c.Attribute("t");
        return t switch
        {
            "s" => int.TryParse(c.Element(M + "v")?.Value, out var i) && i < shared.Count ? shared[i] : "",
            "inlineStr" => string.Concat(c.Element(M + "is")?.Descendants(M + "t").Select(x => x.Value) ?? []),
            _ => c.Element(M + "v")?.Value ?? "",
        };
    }

    private static int ColIndex(string cellRef)
    {
        int n = 0;
        foreach (var ch in cellRef) { if (!char.IsLetter(ch)) break; n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1); }
        return n;
    }

    /// <summary>Excel stores dates as serial numbers; accept those or ISO text.</summary>
    public static DateOnly? ParseDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial > 1000)
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        return DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d
             : DateTime.TryParse(s, CultureInfo.InvariantCulture, out var dt) ? DateOnly.FromDateTime(dt) : null;
    }
}
