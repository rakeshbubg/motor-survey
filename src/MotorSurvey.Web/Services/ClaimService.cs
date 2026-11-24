using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MotorSurvey.Core;

namespace MotorSurvey.Web.Services;

public sealed class ClaimService(
    IClaimStore store, IFileStore files, IErpClient erp, IDamageDetector detector, ILinkSender sender,
    AnalysisQueue queue, ReportRenderer reports, IOptions<SurveyOptions> opt, JsonSerializerOptions json,
    TimeProvider clock, ILogger<ClaimService> log)
{
    private readonly SurveyOptions _o = opt.Value;
    private DateTimeOffset Now => clock.GetUtcNow();

    // ---------- claims desk ----------
    public async Task<(Claim claim, string link, string message)> RegisterAsync(NewClaimRequest req, string baseUrl, CancellationToken ct)
    {
        var p = await erp.GetPolicyAsync(req.PolicyNo, ct) ?? throw new AppError(404, "Policy not found in ERP");
        var claimNo = string.IsNullOrWhiteSpace(req.ClaimNo) ? NewClaimNo() : req.ClaimNo.Trim();
        if (await store.GetAsync(claimNo, ct) != null) throw new AppError(409, $"Claim {claimNo} already exists");
        var c = new Claim
        {
            ClaimNo = claimNo, PolicyNo = p.PolicyNo, CustomerName = p.CustomerName, Mobile = p.Mobile, Email = p.Email,
            Vehicle = new VehicleInfo { Make = p.Make, Model = p.Model, Year = p.Year, Colour = p.Colour, PlateNo = p.PlateNo, Vin = p.Vin },
            IncidentDate = req.IncidentDate, Handler = string.IsNullOrWhiteSpace(req.Handler) ? p.UwHandler : req.Handler!,
            Channel = req.Channel ?? "WhatsApp", CreatedAt = Now,
        };
        NewToken(c);
        await store.SaveAsync(c, ct);
        var (link, msg) = await SendLinkAsync(c, baseUrl, ct);
        return (c, link, msg);
    }

    public async Task<(Claim claim, string link, string message)> ResendAsync(string claimNo, string baseUrl, CancellationToken ct)
    {
        var c = await Required(claimNo, ct);
        if (c.Status is ClaimStatus.Filed or ClaimStatus.ErpFailed) throw new AppError(409, "Survey already signed");
        NewToken(c);
        await store.SaveAsync(c, ct);
        var (link, msg) = await SendLinkAsync(c, baseUrl, ct);
        return (c, link, msg);
    }

    private void NewToken(Claim c)
    {
        c.Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        c.ExpiresAt = Now.AddHours(_o.LinkValidHours);
    }

    private async Task<(string, string)> SendLinkAsync(Claim c, string baseUrl, CancellationToken ct)
    {
        var link = $"{(string.IsNullOrWhiteSpace(_o.PublicBaseUrl) ? baseUrl : _o.PublicBaseUrl).TrimEnd('/')}/s/{c.Token}";
        var until = c.ExpiresAt.ToOffset(TimeSpan.FromHours(3)).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
        var msg = $"Dear {c.CustomerName}, please complete the vehicle survey for claim {c.ClaimNo} using this secure link (valid until {until}):\n{link}\n" +
                  $"عزيزي العميل، يرجى إكمال معاينة المركبة للمطالبة رقم {c.ClaimNo} من خلال الرابط أعلاه.\n— ILAF Takaful";
        c.LastDelivery = await sender.SendAsync(c, link, msg, ct);
        await store.SaveAsync(c, ct);
        return (link, msg);
    }

    public async Task ReviewAsync(string claimNo, string? note, CancellationToken ct)
    {
        var c = await Required(claimNo, ct);
        c.Reviewed = true; c.ReviewedAt = Now; c.ReviewNote = note;
        await store.SaveAsync(c, ct);
    }

    // ---------- client survey ----------
    public async Task<Claim> OpenAsync(string token, CancellationToken ct)
    {
        var c = await store.FindByTokenAsync(token, ct) ?? throw new AppError(404, "link-invalid");
        if (c.Status is ClaimStatus.Filed or ClaimStatus.ErpFailed) throw new AppError(410, "link-used");
        if (Now >= c.ExpiresAt) throw new AppError(410, "link-expired");
        return c;
    }

    public async Task<Claim> ConsentAsync(string token, ConsentRequest req, CancellationToken ct)
    {
        var c = await OpenAsync(token, ct);
        if (!req.Agree) throw new AppError(400, "Consent is required");
        c.ConsentAt ??= Now;
        c.ConsentLocation = req.Lat is { } la && req.Lng is { } ln ? new GeoPoint(la, ln, req.Acc) : null;
        c.Language = req.Language == "ar" ? "ar" : "en";
        if (c.Status == ClaimStatus.LinkSent) c.Status = ClaimStatus.InSurvey;
        await store.SaveAsync(c, ct);
        return c;
    }

    public async Task<Photo> AddPhotoAsync(string token, PhotoUpload up, CancellationToken ct)
    {
        var c = await OpenAsync(token, ct);
        if (c.ConsentAt is null) throw new AppError(400, "Consent is required before photos");
        if (c.Status is ClaimStatus.Analysing) throw new AppError(409, "Photos are being analysed");
        var slot = Slots.Find(up.Slot) ?? throw new AppError(400, "Unknown photo position");
        if (up.Length == 0 || up.Length > _o.MaxPhotoMb * 1024L * 1024) throw new AppError(413, "Photo is empty or too large");

        // read once: validate it is really a JPEG/PNG/WebP and hash it
        using var ms = new MemoryStream();
        await up.Data.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var (ext, mime) = Sniff(bytes) ?? throw new AppError(415, "Only JPEG, PNG or WebP photos are accepted");
        var id = Guid.NewGuid().ToString("N")[..12];
        ms.Position = 0;
        var path = await files.SaveAsync(c.ClaimNo, $"{slot.Key}_{id}{ext}", ms, ct);

        var photo = new Photo
        {
            Id = id, Slot = slot.Key, Label = slot.Label, Path = path, ContentType = mime, Size = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), ReceivedAt = Now,
            CapturedAtClient = up.CapturedAt, LiveCamera = up.Live,
            Location = up.Lat is { } la && up.Lng is { } ln ? new GeoPoint(la, ln, up.Acc) : null,
            Quality = string.IsNullOrWhiteSpace(up.QualityIssues) ? "ok" : up.QualityIssues!, QualityOverride = up.QualityOverride,
        };
        c.Photos.RemoveAll(x => x.Slot == slot.Key);   // retake replaces the previous photo for that position
        c.Photos.Add(photo);
        if (c.Status == ClaimStatus.ReadyToSign) { c.Status = ClaimStatus.InSurvey; c.AiStatus = "pending"; c.Findings.Clear(); }
        await store.SaveAsync(c, ct);
        return photo;
    }

    public async Task<Claim> RequestAnalysisAsync(string token, CancellationToken ct)
    {
        var c = await OpenAsync(token, ct);
        var missing = Slots.All.Where(s => s.Required && c.Photos.All(p => p.Slot != s.Key)).Select(s => s.Label).ToList();
        if (missing.Count > 0) throw new AppError(400, "Missing required photos: " + string.Join(", ", missing));   // BR-05
        if (c.Status != ClaimStatus.Analysing)
        {
            c.Status = ClaimStatus.Analysing; c.AiStatus = "pending";
            await store.SaveAsync(c, ct);
            await queue.EnqueueAsync(c.ClaimNo);
        }
        return c;
    }

    /// <summary>Runs on the background worker (BR-07).</summary>
    public async Task RunAnalysisAsync(string claimNo, CancellationToken ct)
    {
        var c = await Required(claimNo, ct);
        if (c.Status != ClaimStatus.Analysing) return;
        var findings = new List<Finding>();
        string? issue = null;
        try
        {
            foreach (var p in c.Photos.OrderBy(p => Slots.All.ToList().FindIndex(s => s.Key == p.Slot)))
            {
                var r = await detector.DetectAsync(files.FullPath(p.Path), Slots.Find(p.Slot)!, ct);
                issue ??= r.Issue;
                c.Vehicle.PlateRead ??= r.PlateText; c.Vehicle.VinRead ??= r.VinText; c.Vehicle.Odometer ??= r.OdometerText;
                findings.AddRange(r.Damages.Select(d => new Finding
                {
                    Type = d.Type, Part = d.Part, Confidence = Math.Round(d.Confidence, 3), PhotoId = p.Id, PhotoLabel = p.Label,
                    Severity = SeverityRules.For(d.Type, d.W * d.H), Box = [d.X, d.Y, d.W, d.H],
                    NeedsReview = d.Confidence < _o.ReviewConfidence,
                }));
            }
            c.AiStatus = !detector.IsReady ? "no-model" : "ok";
            c.AiMessage = !detector.IsReady ? "AI model not installed yet — every survey goes to manual UW review." : issue;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Detector failed on {Claim}", claimNo);
            c.AiStatus = "error"; c.AiMessage = "AI check failed — sent to manual UW review.";
        }
        c.DetectorName = detector.Name;
        c.Findings = findings;
        c.Checks = BuildChecks(c);
        c.Flagged = c.AiStatus != "ok" || findings.Any(f => f.NeedsReview) || c.Checks.Any(x => x.Ok == false);
        c.Status = ClaimStatus.ReadyToSign;
        await store.SaveAsync(c, ct);
    }

    private static List<EvidenceCheck> BuildChecks(Claim c)
    {
        static string N(string? s) => new((s ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        var list = new List<EvidenceCheck>();
        if (!string.IsNullOrEmpty(c.Vehicle.PlateRead))
        {
            var ok = N(c.Vehicle.PlateRead).Contains(N(c.Vehicle.PlateNo)) || N(c.Vehicle.PlateNo).Contains(N(c.Vehicle.PlateRead));
            list.Add(new(ok, ok ? $"Plate read ({c.Vehicle.PlateRead}) matches policy" : $"Plate read ({c.Vehicle.PlateRead}) does not match policy plate {c.Vehicle.PlateNo}"));
        }
        else list.Add(new(null, "Plate not read automatically — check the plate photo"));
        if (!string.IsNullOrEmpty(c.Vehicle.VinRead))
        {
            var ok = N(c.Vehicle.VinRead) == N(c.Vehicle.Vin);
            list.Add(new(ok, ok ? "VIN matches policy" : $"VIN read ({c.Vehicle.VinRead}) differs from policy"));
        }
        var gps = c.Photos.Count(p => p.Location != null);
        list.Add(new(gps == c.Photos.Count, gps == c.Photos.Count ? "GPS location on every photo" : $"{c.Photos.Count - gps} photo(s) without GPS location"));
        var times = c.Photos.Select(p => p.ReceivedAt).ToList();
        var span = times.Count > 0 ? (times.Max() - times.Min()).TotalMinutes : 0;
        list.Add(new(span <= 60, $"All photos taken within {Math.Max(1, Math.Round(span))} min"));
        var notLive = c.Photos.Count(p => !p.LiveCamera);
        if (notLive > 0) list.Add(new(false, $"{notLive} photo(s) not taken with the live camera"));
        var q = c.Photos.Count(p => p.QualityOverride);
        if (q > 0) list.Add(new(false, $"{q} photo(s) kept despite a quality warning"));
        var dup = c.Photos.GroupBy(p => p.Sha256).Count(g => g.Count() > 1);
        if (dup > 0) list.Add(new(false, "The same photo was used for more than one position"));
        return list;
    }

    public async Task<Claim> SignAsync(string token, SignRequest req, CancellationToken ct)
    {
        var c = await OpenAsync(token, ct);
        if (c.Status != ClaimStatus.ReadyToSign) throw new AppError(409, "The report is not ready to sign yet");
        if (!req.Agree) throw new AppError(400, "Please confirm the declaration");
        var sig = ParsePngDataUrl(req.SignaturePng) ?? throw new AppError(400, "Signature missing");

        c.SignaturePath = await files.SaveAsync(c.ClaimNo, "signature.png", new MemoryStream(sig), ct);
        c.SignedAt = Now;
        c.ReportHash = ComputeReportHash(c, Convert.ToHexStringLower(SHA256.HashData(sig)));
        var html = reports.Render(c, embedImages: true);
        c.ReportPath = await files.SaveAsync(c.ClaimNo, "report.html", new MemoryStream(Encoding.UTF8.GetBytes(html)), ct);
        c.Status = ClaimStatus.ErpFailed; // becomes Filed once the ERP accepts it
        await store.SaveAsync(c, ct);
        await PushToErpAsync(c, ct);
        return c;
    }

    public async Task<Claim> RetryErpAsync(string claimNo, CancellationToken ct)
    {
        var c = await Required(claimNo, ct);
        if (c.Status != ClaimStatus.ErpFailed) throw new AppError(409, "Nothing to retry");
        await PushToErpAsync(c, ct);
        return c;
    }

    private async Task PushToErpAsync(Claim c, CancellationToken ct)
    {
        var pkg = new SurveyPackage(c,
            [new("report.html", "text/html", files.FullPath(c.ReportPath!)), new("signature.png", "image/png", files.FullPath(c.SignaturePath!)),
             .. c.Photos.Select(p => new PackageFile($"{p.Slot}{Path.GetExtension(p.Path)}", p.ContentType, files.FullPath(p.Path)))],
            JsonSerializer.Serialize(Payload(c), json), Summary(c));
        var r = await erp.UploadSurveyAsync(pkg, ct);
        if (r.Ok) { c.Status = ClaimStatus.Filed; c.ErpRef = r.Reference; c.ErpError = null; c.FiledAt = Now; }
        else { c.Status = ClaimStatus.ErpFailed; c.ErpError = r.Error; }
        await store.SaveAsync(c, ct);
    }

    public object Payload(Claim c) => new
    {
        c.ClaimNo, c.PolicyNo, c.CustomerName, status = "SURVEY_SIGNED", c.Vehicle, c.IncidentDate,
        findings = c.Findings.Select(f => new { f.Type, f.Part, f.Severity, f.Confidence, photo = f.PhotoLabel, f.NeedsReview }),
        photos = c.Photos.Select(p => new { position = p.Label, p.Sha256, capturedAt = p.ReceivedAt, gps = p.Location, p.LiveCamera, p.Quality }),
        checks = c.Checks,
        signature = new { signedBy = c.CustomerName, c.SignedAt, reportSha256 = c.ReportHash },
        routing = new { department = "Underwriting", handler = c.Handler, flaggedForReview = c.Flagged, ai = c.AiStatus },
    };

    private static string Summary(Claim c) =>
        $"Motor self-survey filed — claim {c.ClaimNo}\n" +
        $"Customer: {c.CustomerName} · Vehicle: {c.Vehicle.Make} {c.Vehicle.Model} {c.Vehicle.Year} · Plate {c.Vehicle.PlateNo}\n" +
        $"Findings: {c.Findings.Count} ({string.Join(", ", c.Findings.GroupBy(f => f.Severity).Select(g => $"{g.Count()} {g.Key}"))})\n" +
        $"Signed: {c.SignedAt:yyyy-MM-dd HH:mm} UTC · SHA-256 {c.ReportHash}\n" +
        (c.Flagged ? $"⚠ Flagged for UW review — handler {c.Handler}" : $"Handler: {c.Handler}");

    private string ComputeReportHash(Claim c, string signatureSha)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            c.ClaimNo, c.PolicyNo, c.Vehicle,
            findings = c.Findings.Select(f => new { f.Type, f.Part, f.Severity, f.Confidence, f.PhotoId }),
            photos = c.Photos.OrderBy(p => p.Slot, StringComparer.Ordinal).Select(p => new { p.Slot, p.Sha256, p.ReceivedAt, p.Location }),
            c.SignedAt, signatureSha,
        }, json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // ---------- helpers ----------
    public async Task<Claim> Required(string claimNo, CancellationToken ct) => await store.GetAsync(claimNo, ct) ?? throw new AppError(404, "Claim not found");
    private static string NewClaimNo() => $"CLM-{DateTime.UtcNow:yy}-{RandomNumberGenerator.GetInt32(100000, 999999)}";

    private static (string ext, string mime)? Sniff(byte[] b) =>
        b.Length > 12 && b[0] == 0xFF && b[1] == 0xD8 ? (".jpg", "image/jpeg")
        : b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? (".png", "image/png")
        : b.Length > 12 && Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "WEBP" ? (".webp", "image/webp")
        : null;

    private static byte[]? ParsePngDataUrl(string? s)
    {
        const string prefix = "data:image/png;base64,";
        if (s is null || !s.StartsWith(prefix, StringComparison.Ordinal) || s.Length > 2_000_000) return null;
        try { var b = Convert.FromBase64String(s[prefix.Length..]); return Sniff(b)?.mime == "image/png" ? b : null; } catch { return null; }
    }
}

public sealed record NewClaimRequest(string PolicyNo, string? ClaimNo, DateOnly? IncidentDate, string? Handler, string? Channel);
public sealed record ConsentRequest(bool Agree, double? Lat, double? Lng, double? Acc, string? Language);
public sealed record SignRequest(bool Agree, string? SignaturePng);
public sealed record PhotoUpload(string Slot, Stream Data, long Length, double? Lat, double? Lng, double? Acc,
    DateTimeOffset? CapturedAt, bool Live, string? QualityIssues, bool QualityOverride);
public sealed class AppError(int status, string message) : Exception(message) { public int Status { get; } = status; }
