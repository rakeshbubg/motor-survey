using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using MotorSurvey.Core;

namespace MotorSurvey.Web.Erp;

/// <summary>What the ERP connection tab saves (data/erp_settings.json). The Odoo API key is stored encrypted.</summary>
public sealed class ErpSettings
{
    public string Provider { get; set; } = "Excel";          // Excel | Odoo
    public string ExcelPath { get; set; } = "";
    public OdooOptions Odoo { get; set; } = new();
    public string? OdooKeyProtected { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// The IErpClient the app uses. Delegates to the ERP chosen in the ERP connection tab and can switch
/// between Excel and Odoo at runtime, without a restart.
/// </summary>
public sealed class ErpManager : IErpClient
{
    public static readonly string[] Providers = ["Excel", "Odoo"];

    private readonly string _file, _dataDir;
    private readonly IHttpClientFactory _http;
    private readonly ILoggerFactory _logs;
    private readonly IDataProtector _protect;
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IErpClient _current;
    private ErpSettings _settings;

    public ErpManager(IConfiguration cfg, string contentRoot, string dataDir, IHttpClientFactory http, ILoggerFactory logs,
                      IDataProtectionProvider dp, JsonSerializerOptions json)
    {
        _dataDir = dataDir; _http = http; _logs = logs; _json = json;
        _protect = dp.CreateProtector("MotorSurvey.Erp.v1");
        _file = Path.Combine(dataDir, "erp_settings.json");

        ErpSettings? saved = null;
        if (File.Exists(_file)) { try { saved = JsonSerializer.Deserialize<ErpSettings>(File.ReadAllText(_file), json); } catch { } }
        _settings = saved ?? FromConfig(cfg, contentRoot);
        _current = Build(_settings, null);
    }

    private ErpSettings FromConfig(IConfiguration cfg, string contentRoot)
    {
        var odoo = cfg.GetSection("Odoo").Get<OdooOptions>() ?? new();
        var key = odoo.ApiKey; odoo.ApiKey = "";
        return new ErpSettings
        {
            Provider = string.Equals(cfg["Erp:Provider"], "Odoo", StringComparison.OrdinalIgnoreCase) ? "Odoo" : "Excel",
            ExcelPath = Path.GetFullPath(Path.Combine(contentRoot, cfg["Erp:ExcelPath"] ?? "data/erp_customers.xlsx")),
            Odoo = odoo,
            OdooKeyProtected = string.IsNullOrEmpty(key) ? null : _protect.Protect(key),
        };
    }

    public ErpSettings Settings => _settings;
    public bool HasOdooKey => !string.IsNullOrEmpty(_settings.OdooKeyProtected);
    public string DataDir => _dataDir;

    /// <summary>Builds a client from settings. newKey: a key typed on screen; null/blank = keep the saved one.</summary>
    public IErpClient Build(ErpSettings s, string? newKey)
    {
        if (s.Provider == "Odoo")
        {
            var o = Clone(s.Odoo);
            o.ApiKey = !string.IsNullOrWhiteSpace(newKey) ? newKey.Trim()
                     : s.OdooKeyProtected is { } p ? SafeUnprotect(p) : "";
            return new OdooErpClient(_http.CreateClient("odoo"), o, _logs.CreateLogger<OdooErpClient>());
        }
        return new ExcelErpClient(s.ExcelPath, Path.Combine(_dataDir, "erp_outbox"), _logs.CreateLogger<ExcelErpClient>());
    }

    public async Task SaveAsync(ErpSettings s, string? newKey, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            s.OdooKeyProtected = !string.IsNullOrWhiteSpace(newKey) ? _protect.Protect(newKey.Trim()) : _settings.OdooKeyProtected;
            s.Odoo.ApiKey = "";
            s.UpdatedAt = DateTimeOffset.UtcNow;
            var client = Build(s, newKey);
            await File.WriteAllTextAsync(_file, JsonSerializer.Serialize(s, _json), ct);
            _settings = s; _current = client;
        }
        finally { _lock.Release(); }
    }

    private string SafeUnprotect(string p) { try { return _protect.Unprotect(p); } catch { return ""; } }
    private OdooOptions Clone(OdooOptions o) => JsonSerializer.Deserialize<OdooOptions>(JsonSerializer.Serialize(o, _json), _json)!;

    // ---- IErpClient: delegate to the active ERP ----
    public string Name => _current.Name;
    public Task<IReadOnlyList<Policy>> GetPoliciesAsync(CancellationToken ct) => _current.GetPoliciesAsync(ct);
    public Task<Policy?> GetPolicyAsync(string policyNo, CancellationToken ct) => _current.GetPolicyAsync(policyNo, ct);
    public Task<ErpUploadResult> UploadSurveyAsync(SurveyPackage package, CancellationToken ct) => _current.UploadSurveyAsync(package, ct);
}
