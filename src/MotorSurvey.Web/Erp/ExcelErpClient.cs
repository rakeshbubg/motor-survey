using System.Globalization;
using System.Text;
using MotorSurvey.Core;

namespace MotorSurvey.Web.Erp;

/// <summary>
/// Mock ERP for the pilot: reads policies from data/erp_customers.xlsx (sheet "Policies") and
/// "uploads" each signed survey to data/erp_outbox/&lt;ClaimNo&gt;/ plus a row in erp_outbox/surveys.csv (opens in Excel).
/// </summary>
public sealed class ExcelErpClient(string workbookPath, string outboxFolder, ILogger<ExcelErpClient> log) : IErpClient
{
    public string Name => "Excel (" + Path.GetFileName(workbookPath) + ")";
    private static readonly SemaphoreSlim CsvLock = new(1, 1);

    public Task<IReadOnlyList<Policy>> GetPoliciesAsync(CancellationToken ct)
    {
        // Re-read on every call so edits to the Excel file show up without a restart.
        var rows = XlsxReader.ReadSheet(workbookPath, "Policies");
        IReadOnlyList<Policy> list = rows.Where(r => !string.IsNullOrWhiteSpace(Get(r, "PolicyNo"))).Select(r => new Policy(
            Get(r, "PolicyNo"), Get(r, "CustomerName"), Get(r, "Mobile"), Get(r, "Email"),
            Get(r, "Make"), Get(r, "Model"), int.TryParse(Get(r, "Year"), out var y) ? y : 0, Get(r, "Colour"),
            Get(r, "PlateNo"), Get(r, "VIN"), Get(r, "CoverType"),
            XlsxReader.ParseDate(Get(r, "PolicyStart")), XlsxReader.ParseDate(Get(r, "PolicyEnd")),
            decimal.TryParse(Get(r, "SumInsuredKWD"), NumberStyles.Any, CultureInfo.InvariantCulture, out var si) ? si : null,
            Get(r, "UwHandler"))).ToList();
        return Task.FromResult(list);
    }

    public async Task<Policy?> GetPolicyAsync(string policyNo, CancellationToken ct) =>
        (await GetPoliciesAsync(ct)).FirstOrDefault(p => string.Equals(p.PolicyNo, policyNo, StringComparison.OrdinalIgnoreCase));

    public async Task<ErpUploadResult> UploadSurveyAsync(SurveyPackage pkg, CancellationToken ct)
    {
        try
        {
            var c = pkg.Claim;
            var dir = Path.Combine(outboxFolder, c.ClaimNo);
            Directory.CreateDirectory(dir);
            foreach (var f in pkg.Files) File.Copy(f.LocalPath, Path.Combine(dir, f.FileName), overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(dir, "payload.json"), pkg.PayloadJson, ct);

            var csv = Path.Combine(outboxFolder, "surveys.csv");
            await CsvLock.WaitAsync(ct);
            try
            {
                if (!File.Exists(csv))
                    await File.WriteAllTextAsync(csv, "﻿FiledAt,ClaimNo,PolicyNo,Customer,Plate,Findings,FlaggedForReview,Handler,ReportSha256,Folder\r\n", ct);
                var line = string.Join(",", new[] {
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"), c.ClaimNo, c.PolicyNo, c.CustomerName, c.Vehicle.PlateNo,
                    c.Findings.Count.ToString(CultureInfo.InvariantCulture), c.Flagged ? "Yes" : "No", c.Handler, c.ReportHash ?? "", dir }
                    .Select(Csv));
                await File.AppendAllTextAsync(csv, line + "\r\n", Encoding.UTF8, ct);
            }
            finally { CsvLock.Release(); }

            log.LogInformation("Survey {Claim} filed to Excel ERP outbox; UW handler {Handler} notified", c.ClaimNo, c.Handler);
            return new ErpUploadResult(true, "XLS-" + c.ClaimNo, null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Excel ERP upload failed");
            return new ErpUploadResult(false, null, ex.Message);
        }
    }

    private static string Get(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) ? v : "";
    private static string Csv(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
