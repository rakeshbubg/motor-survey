using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using MotorSurvey.Core;
using MotorSurvey.Web.Erp;
using MotorSurvey.Web.Services;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.Configure<SurveyOptions>(cfg.GetSection("Survey"));
var survey = cfg.GetSection("Survey").Get<SurveyOptions>() ?? new();
var dataDir = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, survey.DataFolder));
Directory.CreateDirectory(dataDir);

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(json);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IClaimStore>(new JsonClaimStore(dataDir, json));
builder.Services.AddSingleton<IFileStore>(new LocalFileStore(dataDir));
builder.Services.Configure<MessagingOptions>(cfg.GetSection("Messaging"));
builder.Services.AddHttpClient("twilio", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<ILinkSender, LinkSender>();
builder.Services.AddSingleton<ReportRenderer>();
builder.Services.AddSingleton<AnalysisQueue>();
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddScoped<ClaimService>();

// ---- ERP: chosen and configured in the staff console's "ERP connection" tab (Excel or Odoo) ----
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys"))).SetApplicationName("MotorSurvey");
builder.Services.AddHttpClient("odoo", c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton(sp => new ErpManager(cfg, builder.Environment.ContentRootPath, dataDir, sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), json));
builder.Services.AddSingleton<IErpClient>(sp => sp.GetRequiredService<ErpManager>());

// ---- AI engine: ONNX model if built with -p:UseOnnx=true and the model file exists ----
// ---- AI engine: Gemini (if a key is set), else the ONNX model, else manual review ----
var gemini = cfg.GetSection("Ai:Gemini").Get<GeminiOptions>() ?? new();
if (!string.IsNullOrWhiteSpace(gemini.ApiKey))
{
    builder.Services.AddHttpClient("gemini", c => c.Timeout = TimeSpan.FromSeconds(gemini.TimeoutSeconds + 10));
    builder.Services.AddSingleton<IDamageDetector>(sp => new GeminiDamageDetector(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("gemini"), gemini, sp.GetRequiredService<ILogger<GeminiDamageDetector>>()));
}
else
{
#if USE_ONNX
var ai = cfg.GetSection("Ai").Get<MotorSurvey.Ai.Onnx.OnnxDetectorOptions>() ?? new();
ai.ModelPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, ai.ModelPath));
if (File.Exists(ai.ModelPath)) builder.Services.AddSingleton<IDamageDetector>(new MotorSurvey.Ai.Onnx.OnnxDamageDetector(ai));
else builder.Services.AddSingleton<IDamageDetector, NoModelDetector>();
#else
builder.Services.AddSingleton<IDamageDetector, NoModelDetector>();
#endif
}

// ---- limits ----
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = (survey.MaxPhotoMb + 2) * 1024L * 1024);
builder.Services.Configure<FormOptions>(f => f.MultipartBodyLengthLimit = (survey.MaxPhotoMb + 1) * 1024L * 1024);
builder.Services.AddRateLimiter(r =>
{
    r.RejectionStatusCode = 429;
    r.AddPolicy("survey", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "x",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(self), geolocation=(self)";
    try { await next(); }
    catch (AppError e) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = e.Status;
        await ctx.Response.WriteAsJsonAsync(new { error = e.Message });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
// The car-check model's weight files have no extension ("group1-shard1of2"): serve them from this one folder only.
var modelDir = Path.Combine(app.Environment.WebRootPath, "lib", "coco-ssd-lite");
if (Directory.Exists(modelDir))
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(modelDir),
        RequestPath = "/lib/coco-ssd-lite", ServeUnknownFileTypes = true, DefaultContentType = "application/octet-stream",
        OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "public,max-age=604800",
    });

string BaseUrl(HttpRequest r) => $"{r.Scheme}://{r.Host}";
string? Url(string? path, string key, string claimNo, string id) => path == null ? null : $"/api/staff/claims/{Uri.EscapeDataString(claimNo)}/files/{id}?k={Uri.EscapeDataString(key)}";

// ---------- client survey (link holder) ----------
app.MapGet("/s/{token}", (IWebHostEnvironment env) => Results.File(Path.Combine(env.WebRootPath, "survey.html"), "text/html"));

var s = app.MapGroup("/api/survey/{token}").RequireRateLimiting("survey");
object ClientView(Claim c, SurveyOptions o) => new
{
    c.ClaimNo, c.CustomerName, c.Status, c.ExpiresAt, c.Language, consent = c.ConsentAt != null,
    vehicle = new { c.Vehicle.Make, c.Vehicle.Model, c.Vehicle.Year, c.Vehicle.Colour, c.Vehicle.PlateNo, c.Vehicle.Vin, c.Vehicle.Odometer },
    slots = Slots.All, photos = c.Photos.Select(p => new { p.Slot, p.ReceivedAt, p.Quality, gps = p.Location != null }),
    c.AiStatus, c.AiMessage, c.Flagged, c.Findings, c.Checks, o.AllowFileFallback, o.AllowGallery, o.RequireGps, o.MaxPhotoMb,
};
s.MapGet("", async (string token, ClaimService svc, IOptions<SurveyOptions> o, CancellationToken ct) => Results.Ok(ClientView(await svc.OpenAsync(token, ct), o.Value)));
s.MapPost("/consent", async (string token, ConsentRequest req, ClaimService svc, IOptions<SurveyOptions> o, CancellationToken ct) => Results.Ok(ClientView(await svc.ConsentAsync(token, req, ct), o.Value)));
s.MapPost("/photos", async (string token, HttpRequest http, ClaimService svc, CancellationToken ct) =>
{
    var form = await http.ReadFormAsync(ct);
    var file = form.Files.GetFile("photo") ?? throw new AppError(400, "photo missing");
    static double? D(string? v) => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    await using var stream = file.OpenReadStream();
    var p = await svc.AddPhotoAsync(token, new PhotoUpload(form["slot"].ToString(), stream, file.Length, D(form["lat"]), D(form["lng"]), D(form["acc"]),
        DateTimeOffset.TryParse(form["capturedAt"], out var t) ? t : null, form["live"] == "true", form["quality"], form["qualityOverride"] == "true"), ct);
    return Results.Ok(new { p.Slot, p.ReceivedAt, p.Quality, gps = p.Location != null });
}).DisableAntiforgery();
s.MapGet("/photos/{slot}", async (string token, string slot, ClaimService svc, IFileStore files, CancellationToken ct) =>
{
    var c = await svc.OpenAsync(token, ct);
    var p = c.Photos.FirstOrDefault(x => x.Slot == slot);
    return p == null ? Results.NotFound() : Results.File(files.FullPath(p.Path), p.ContentType);
});
s.MapPost("/analyze", async (string token, ClaimService svc, IOptions<SurveyOptions> o, CancellationToken ct) => Results.Ok(ClientView(await svc.RequestAnalysisAsync(token, ct), o.Value)));
s.MapPost("/sign", async (string token, SignRequest req, ClaimService svc, CancellationToken ct) =>
{
    var c = await svc.SignAsync(token, req, ct);
    return Results.Ok(new { c.ClaimNo, c.Status, c.ReportHash, c.Handler, c.Flagged, filed = c.Status == ClaimStatus.Filed });
});

// ---------- staff: claims desk + UW (header X-Staff-Key or ?k=) ----------
var staff = app.MapGroup("/api/staff").AddEndpointFilter(async (ctx, next) =>
{
    var o = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<SurveyOptions>>().Value;
    var r = ctx.HttpContext.Request;
    var key = r.Headers["X-Staff-Key"].FirstOrDefault() ?? r.Query["k"].FirstOrDefault();
    return string.IsNullOrEmpty(o.StaffKey) || key == o.StaffKey ? await next(ctx) : Results.Json(new { error = "staff-key" }, statusCode: 401);
});
staff.MapGet("/info", (IErpClient erp, IDamageDetector det, IOptions<SurveyOptions> o, IOptions<MessagingOptions> m) =>
    Results.Ok(new { erp = erp.Name, ai = det.Name, aiReady = det.IsReady, o.Value.LinkValidHours, o.Value.ReviewConfidence,
        email = m.Value.Email.IsConfigured, sms = m.Value.Twilio.IsConfigured }));
staff.MapGet("/policies", async (IErpClient erp, CancellationToken ct) => Results.Ok(await erp.GetPoliciesAsync(ct)));
staff.MapPost("/claims", async (NewClaimRequest req, HttpRequest http, ClaimService svc, CancellationToken ct) =>
{
    var (c, link, msg) = await svc.RegisterAsync(req, BaseUrl(http), ct);
    return Results.Ok(new { claim = Summary(c), link, message = msg });
});
staff.MapPost("/claims/{no}/resend", async (string no, HttpRequest http, ClaimService svc, CancellationToken ct) =>
{
    var (c, link, msg) = await svc.ResendAsync(no, BaseUrl(http), ct);
    return Results.Ok(new { claim = Summary(c), link, message = msg });
});
staff.MapGet("/claims", async (string? q, DateOnly? from, DateOnly? to, string? status, IClaimStore store, CancellationToken ct) =>
{
    static string N(string? x) => new((x ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    var nq = N(q);
    var list = (await store.ListAsync(ct)).Where(c =>
        (nq == "" || new[] { c.ClaimNo, c.PolicyNo, c.CustomerName, c.Vehicle.PlateNo, c.Vehicle.Vin }.Any(x => N(x).Contains(nq))) &&
        (from == null || DateOnly.FromDateTime(c.CreatedAt.UtcDateTime) >= from) && (to == null || DateOnly.FromDateTime(c.CreatedAt.UtcDateTime) <= to) &&
        (status switch { "flag" => c.Flagged && !c.Reviewed && c.Status == ClaimStatus.Filed, "filed" => c.Status == ClaimStatus.Filed,
                         "open" => c.Status < ClaimStatus.Filed, "erp" => c.Status == ClaimStatus.ErpFailed, _ => true }));
    return Results.Ok(list.Take(500).Select(Summary));
});
staff.MapGet("/claims/{no}", async (string no, HttpRequest http, ClaimService svc, CancellationToken ct) =>
{
    var c = await svc.Required(no, ct);
    var key = http.Headers["X-Staff-Key"].FirstOrDefault() ?? http.Query["k"].FirstOrDefault() ?? "";
    return Results.Ok(new
    {
        summary = Summary(c), c.Vehicle, c.IncidentDate, c.Findings, c.Checks, c.AiMessage, c.AiReview, c.DetectorName, c.ConsentAt, c.SignedAt, c.ReportHash, c.ErpError, c.ReviewNote,
        photos = c.Photos.Select(p => new { p.Id, p.Label, p.ReceivedAt, p.Location, p.LiveCamera, p.Quality, p.QualityOverride, url = Url(p.Path, key, c.ClaimNo, p.Id) }),
        signatureUrl = Url(c.SignaturePath, key, c.ClaimNo, "signature"),
        reportUrl = c.ReportPath == null ? null : $"/api/staff/claims/{Uri.EscapeDataString(c.ClaimNo)}/report?k={Uri.EscapeDataString(key)}",
    });
});
staff.MapGet("/claims/{no}/files/{id}", async (string no, string id, ClaimService svc, IFileStore files, CancellationToken ct) =>
{
    var c = await svc.Required(no, ct);
    if (id == "signature" && c.SignaturePath != null) return Results.File(files.FullPath(c.SignaturePath), "image/png");
    var p = c.Photos.FirstOrDefault(x => x.Id == id);
    return p == null ? Results.NotFound() : Results.File(files.FullPath(p.Path), p.ContentType);
});
staff.MapGet("/claims/{no}/report", async (string no, bool? download, ClaimService svc, IFileStore files, CancellationToken ct) =>
{
    var c = await svc.Required(no, ct);
    if (c.ReportPath == null) return Results.NotFound();
    return Results.File(files.FullPath(c.ReportPath), "text/html; charset=utf-8", download == true ? $"survey-{c.ClaimNo}.html" : null);
});
staff.MapGet("/claims/{no}/payload", async (string no, ClaimService svc, CancellationToken ct) => Results.Ok(svc.Payload(await svc.Required(no, ct))));
staff.MapPost("/claims/{no}/review", async (string no, ReviewRequest req, ClaimService svc, CancellationToken ct) => { await svc.ReviewAsync(no, req.Note, ct); return Results.Ok(); });
staff.MapPost("/claims/{no}/retry-erp", async (string no, ClaimService svc, CancellationToken ct) => Results.Ok(Summary(await svc.RetryErpAsync(no, ct))));

// ---------- staff: ERP connection tab ----------
object ErpView(ErpManager m) => new
{
    active = m.Settings.Provider, name = m.Name, m.Settings.UpdatedAt,
    providers = new object[]
    {
        new { id = "Excel", title = "Excel file (demo ERP)", description = "Policies from an .xlsx file; signed surveys saved to the outbox folder.", available = true },
        new { id = "Odoo", title = "Odoo", description = "Policies from your Odoo model; report and photos attached to the policy with a chatter note.", available = true },
        new { id = "Dynamics", title = "Microsoft Dynamics 365", description = "Planned connector.", available = false },
        new { id = "SAP", title = "SAP Business One", description = "Planned connector.", available = false },
    },
    excel = new { path = m.Settings.ExcelPath, fileName = Path.GetFileName(m.Settings.ExcelPath), exists = File.Exists(m.Settings.ExcelPath) },
    odoo = new { m.Settings.Odoo.Url, m.Settings.Odoo.Database, m.Settings.Odoo.Username, m.Settings.Odoo.Api, m.Settings.Odoo.PolicyModel,
                 m.Settings.Odoo.PostChatterNote, fields = OdooErpClient.AppFields.ToDictionary(f => f, f => m.Settings.Odoo.Fields.GetValueOrDefault(f, "")), hasKey = m.HasOdooKey },
};
ErpSettings FromRequest(ErpManager m, ErpConfigRequest r)
{
    var provider = ErpManager.Providers.FirstOrDefault(p => string.Equals(p, r.Provider, StringComparison.OrdinalIgnoreCase)) ?? throw new AppError(400, "This ERP is not available yet");
    var o = r.Odoo ?? new OdooOptions();
    if (provider == "Odoo")
    {
        if (!Uri.TryCreate(o.Url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) throw new AppError(400, "Enter the Odoo address, e.g. https://yourcompany.odoo.com");
        if (string.IsNullOrWhiteSpace(o.PolicyModel)) throw new AppError(400, "Enter the Odoo model that holds motor policies");
        if (string.IsNullOrWhiteSpace(r.ApiKey) && !m.HasOdooKey) throw new AppError(400, "Enter the Odoo API key");
        o.Fields = o.Fields.Where(kv => OdooErpClient.AppFields.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
        o.Url = o.Url.TrimEnd('/'); o.ApiKey = "";
    }
    return new ErpSettings { Provider = provider, ExcelPath = m.Settings.ExcelPath, Odoo = provider == "Odoo" ? o : m.Settings.Odoo, OdooKeyProtected = m.Settings.OdooKeyProtected };
}
staff.MapGet("/erp", (ErpManager m) => Results.Ok(ErpView(m)));
staff.MapPost("/erp/test", async (ErpConfigRequest r, ErpManager m, CancellationToken ct) =>
{
    var client = m.Build(FromRequest(m, r), r.ApiKey);
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var list = await client.GetPoliciesAsync(ct);
        return Results.Ok(new { ok = true, ms = sw.ElapsedMilliseconds, count = list.Count, sample = list.Take(3) });
    }
    catch (Exception ex) { return Results.Ok(new { ok = false, error = ex.GetBaseException().Message }); }
});
staff.MapPost("/erp/fields", async (ErpConfigRequest r, ErpManager m, CancellationToken ct) =>
{
    if (m.Build(FromRequest(m, r), r.ApiKey) is not OdooErpClient odoo) throw new AppError(400, "Field list is for Odoo only");
    try { return Results.Ok(new { ok = true, fields = (await odoo.GetModelFieldsAsync(ct)).Select(f => new { f.Name, f.Label, f.Type }) }); }
    catch (Exception ex) { return Results.Ok(new { ok = false, error = ex.GetBaseException().Message }); }
});
staff.MapPost("/erp/save", async (ErpConfigRequest r, ErpManager m, CancellationToken ct) =>
{
    await m.SaveAsync(FromRequest(m, r), r.ApiKey, ct);
    return Results.Ok(ErpView(m));
});
staff.MapPost("/erp/excel", async (HttpRequest http, ErpManager m, CancellationToken ct) =>
{
    var form = await http.ReadFormAsync(ct);
    var f = form.Files.GetFile("file") ?? throw new AppError(400, "Choose an .xlsx file");
    if (!f.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || f.Length > 10 * 1024 * 1024) throw new AppError(400, "Upload an .xlsx file under 10 MB");
    var dest = Path.Combine(m.DataDir, "erp_customers.xlsx");
    var tmp = dest + ".upload";
    await using (var fs = File.Create(tmp)) await f.CopyToAsync(fs, ct);
    int count;
    try { count = XlsxReader.ReadSheet(tmp, "Policies").Count(r => !string.IsNullOrWhiteSpace(r.GetValueOrDefault("PolicyNo"))); }
    catch (Exception ex) { File.Delete(tmp); throw new AppError(400, "Could not read the file: " + ex.Message + ". It needs a sheet named Policies with the same columns as the sample."); }
    File.Move(tmp, dest, overwrite: true);
    var s = m.Settings;
    await m.SaveAsync(new ErpSettings { Provider = s.Provider, ExcelPath = dest, Odoo = s.Odoo, OdooKeyProtected = s.OdooKeyProtected }, null, ct);
    return Results.Ok(new { count, view = ErpView(m) });
}).DisableAntiforgery();

app.Run();

static object Summary(Claim c) => new
{
    c.ClaimNo, c.PolicyNo, c.CustomerName, c.Mobile, c.Email, plate = c.Vehicle.PlateNo, vin = c.Vehicle.Vin,
    vehicle = $"{c.Vehicle.Make} {c.Vehicle.Model} {c.Vehicle.Year}", c.Handler, c.Channel, c.LastDelivery, c.CreatedAt, c.ExpiresAt, c.Status,
    photos = c.Photos.Count, findings = c.Findings.Count, c.Flagged, c.Reviewed, aiRec = c.AiReview?.Recommendation, c.ErpRef, c.ErpError, c.FiledAt, c.AiStatus,
};

public sealed record ReviewRequest(string? Note);
public sealed record ErpConfigRequest(string Provider, OdooOptions? Odoo, string? ApiKey);
public partial class Program;
