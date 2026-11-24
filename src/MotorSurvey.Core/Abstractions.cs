namespace MotorSurvey.Core;

/// <summary>The ERP behind the survey: Excel file today, Odoo in production.</summary>
public interface IErpClient
{
    string Name { get; }
    Task<IReadOnlyList<Policy>> GetPoliciesAsync(CancellationToken ct);
    Task<Policy?> GetPolicyAsync(string policyNo, CancellationToken ct);
    /// <summary>Files the signed report, photos and payload against the claim/policy record (BR-11) and notifies UW (BR-12).</summary>
    Task<ErpUploadResult> UploadSurveyAsync(SurveyPackage package, CancellationToken ct);
}

public sealed record SurveyPackage(Claim Claim, IReadOnlyList<PackageFile> Files, string PayloadJson, string SummaryText);
public sealed record PackageFile(string FileName, string ContentType, string LocalPath);
public sealed record ErpUploadResult(bool Ok, string? Reference, string? Error);

/// <summary>The AI engine. Swap implementations without touching the rest of the system.</summary>
public interface IDamageDetector
{
    string Name { get; }
    bool IsReady { get; }
    Task<DetectionResult> DetectAsync(string imagePath, Slots.Slot slot, CancellationToken ct);
}

public sealed record DetectionResult(
    IReadOnlyList<DetectedDamage> Damages,
    string? PlateText = null, string? VinText = null, string? OdometerText = null, string? Issue = null);

/// <summary>One damage found. Box is x, y, w, h as fractions of the image.</summary>
public sealed record DetectedDamage(string Type, string Part, double Confidence, float X, float Y, float W, float H);

public interface IClaimStore
{
    Task SaveAsync(Claim claim, CancellationToken ct);
    Task<Claim?> GetAsync(string claimNo, CancellationToken ct);
    Task<Claim?> FindByTokenAsync(string token, CancellationToken ct);
    Task<IReadOnlyList<Claim>> ListAsync(CancellationToken ct);
}

public interface IFileStore
{
    /// <summary>Saves a file and returns its storage path (relative).</summary>
    Task<string> SaveAsync(string claimNo, string fileName, Stream data, CancellationToken ct);
    string FullPath(string storedPath);
}

public interface ILinkSender
{
    /// <summary>Sends the survey link by the claim's channel. Returns a short delivery note shown to staff (never throws).</summary>
    Task<string> SendAsync(Claim claim, string url, string message, CancellationToken ct);
}

/// <summary>Severity from damage type and size (share of the photo the damage covers).</summary>
public static class SeverityRules
{
    public static string For(string type, double areaFraction) => type switch
    {
        "Dent" when areaFraction >= 0.04 => "High",
        "Dent" => "Medium",
        "Other" when areaFraction >= 0.06 => "High",
        _ when areaFraction >= 0.08 => "High",
        _ when areaFraction >= 0.02 => "Medium",
        _ => "Low",
    };
}
