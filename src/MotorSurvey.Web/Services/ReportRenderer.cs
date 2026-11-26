using System.Globalization;
using System.Net;
using System.Text;
using MotorSurvey.Core;

namespace MotorSurvey.Web.Services;

/// <summary>Builds the survey report as a self-contained HTML page (BR-08, BR-09). Print it to PDF from any browser.</summary>
public sealed class ReportRenderer(IFileStore files)
{
    private static string E(object? v) => WebUtility.HtmlEncode(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
    private static string T(DateTimeOffset? t) => t is null ? "—" : t.Value.ToOffset(TimeSpan.FromHours(3)).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " (AST)";

    public string Render(Claim c, bool embedImages)
    {
        string Img(string path, string mime)
        {
            if (!embedImages) return "";
            var full = files.FullPath(path);
            return File.Exists(full) ? $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(full))}" : "";
        }
        var v = c.Vehicle;
        var sb = new StringBuilder();
        sb.Append($$"""
<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Survey report {{E(c.ClaimNo)}}</title>
<style>
body{font:14px/1.5 Arial,Helvetica,sans-serif;color:#111;margin:0;background:#fff}
.page{max-width:900px;margin:0 auto;padding:28px 20px}
h1{font-size:22px;margin:0 0 4px}h2{font-size:15px;margin:24px 0 8px;padding-bottom:4px;border-bottom:2px solid #111}
.mut{color:#555}.kv{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:8px 16px}
.kv span{display:block;font-size:11px;color:#666}.kv b{font-weight:600;overflow-wrap:anywhere}
table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:6px;border-bottom:1px solid #ddd;vertical-align:top}th{font-size:12px;color:#555}
.High{color:#b3261e;font-weight:700}.Medium{font-weight:700}.flag{background:#ffe600;padding:2px 6px;border-radius:4px;font-size:12px}
.imgs{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:12px}.imgs figure{margin:0}
.imgs img{width:100%;aspect-ratio:4/3;object-fit:cover;border:1px solid #ddd;border-radius:4px}figcaption{font-size:11px;color:#444}
.banner{padding:10px 12px;border-radius:6px;background:#fff4c2;margin-top:12px}
@media print{.page{padding:0}h2{break-after:avoid}figure{break-inside:avoid} }
</style></head><body><div class="page">
<h1>ILAF Takaful · Motor claim survey report</h1>
<div class="mut">Claim {{E(c.ClaimNo)}} · Policy {{E(c.PolicyNo)}} · Signed {{T(c.SignedAt)}}</div>
""");
        if (c.Flagged) sb.Append($"<div class=\"banner\"><b>Flagged for UW review.</b> {E(c.AiMessage)}</div>");
        sb.Append("<h2>Vehicle</h2><div class=\"kv\">");
        foreach (var (k, val) in new (string, object?)[] {
            ("Customer", c.CustomerName), ("Make", v.Make), ("Model", v.Model), ("Year", v.Year), ("Colour", v.Colour),
            ("Plate no.", v.PlateNo), ("VIN / chassis no.", v.Vin), ("Odometer", v.Odometer ?? "Not read"),
            ("Incident date", c.IncidentDate?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)), ("UW handler", c.Handler) })
            sb.Append($"<div><span>{E(k)}</span><b>{E(val ?? "—")}</b></div>");
        sb.Append("</div>");

        sb.Append($"<h2>Findings ({c.Findings.Count})</h2>");
        if (c.Findings.Count == 0)
            sb.Append(c.AiStatus == "ok" ? "<p><b>No visible damage found in the submitted photos.</b></p>" : "<p><b>No automatic findings — manual UW assessment required.</b></p>");
        else
        {
            sb.Append("<table><thead><tr><th>Fault</th><th>Part</th><th>Severity</th><th>Confidence</th><th>Photo</th></tr></thead><tbody>");
            foreach (var f in c.Findings)
                sb.Append($"<tr><td>{E(f.Type)}</td><td>{E(f.Part)}</td><td class=\"{E(f.Severity)}\">{E(f.Severity)}</td><td>{f.Confidence:P0}{(f.NeedsReview ? " <span class=\"flag\">UW review</span>" : "")}</td><td>{E(f.PhotoLabel)}</td></tr>");
            sb.Append("</tbody></table>");
        }

        if (c.AiReview is { } ar)
        {
            var color = ar.Recommendation switch { "Approve" => "#1d6b3a", "Refer" => "#b3261e", _ => "#8a6d00" };
            sb.Append($"<h2>AI review for UW (internal)</h2><p><span style=\"background:{color};color:#fff;padding:3px 10px;border-radius:12px;font-weight:700\">{E(ar.Recommendation)}</span> <span class=\"mut\">confidence {ar.Confidence:P0}</span></p><p>{E(ar.Summary)}</p>");
            if (ar.Reasons.Count > 0) sb.Append("<ul>" + string.Concat(ar.Reasons.Select(r => $"<li>{E(r)}</li>")) + "</ul>");
            sb.Append("<p class=\"mut\" style=\"font-size:11px\">AI suggestion only. The underwriter makes the decision.</p>");
        }
        sb.Append("<h2>Evidence checks</h2><ul>");
        foreach (var ch in c.Checks) sb.Append($"<li>{(ch.Ok == true ? "✓" : ch.Ok == false ? "⚠" : "•")} {E(ch.Text)}</li>");
        sb.Append("</ul>");

        sb.Append($"<h2>Photos ({c.Photos.Count})</h2><div class=\"imgs\">");
        foreach (var p in c.Photos.OrderBy(p => Slots.All.ToList().FindIndex(s => s.Key == p.Slot)))
        {
            var src = Img(p.Path, p.ContentType);
            var gps = p.Location is { } g ? $"{g.Lat.ToString("F5", CultureInfo.InvariantCulture)}, {g.Lng.ToString("F5", CultureInfo.InvariantCulture)}" : "No GPS";
            sb.Append($"<figure>{(src == "" ? "" : $"<img src=\"{src}\" alt=\"{E(p.Label)}\">")}<figcaption><b>{E(p.Label)}</b><br>{T(p.ReceivedAt)}<br>{E(gps)}<br>SHA-256 {E(p.Sha256[..16])}…</figcaption></figure>");
        }
        sb.Append("</div>");

        if (c.SignaturePath != null)
        {
            var sig = Img(c.SignaturePath, "image/png");
            sb.Append($"<h2>Client declaration and signature</h2><p>I confirm the photos are of the insured vehicle, were taken by me for this claim, and I agree with this report.</p>");
            if (sig != "") sb.Append($"<img src=\"{sig}\" alt=\"Signature\" style=\"max-width:320px;border:1px solid #ddd;border-radius:4px\">");
            sb.Append($"<p class=\"mut\">Signed by {E(c.CustomerName)} on {T(c.SignedAt)}<br>Report fingerprint (SHA-256): {E(c.ReportHash)}</p>");
        }
        sb.Append($"<p class=\"mut\" style=\"margin-top:28px;font-size:11px\">Generated by Motor Self-Survey · AI engine: {E(c.DetectorName ?? "—")}</p></div></body></html>");
        return sb.ToString();
    }
}
