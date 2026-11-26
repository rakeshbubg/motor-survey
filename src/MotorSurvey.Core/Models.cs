namespace MotorSurvey.Core;

/// <summary>A motor policy as held in the ERP (Excel mock or Odoo).</summary>
public sealed record Policy(
    string PolicyNo, string CustomerName, string Mobile, string Email,
    string Make, string Model, int Year, string Colour, string PlateNo, string Vin,
    string CoverType, DateOnly? PolicyStart, DateOnly? PolicyEnd, decimal? SumInsured, string UwHandler);

public enum ClaimStatus { LinkSent, InSurvey, Analysing, ReadyToSign, Filed, ErpFailed }

public sealed class Claim
{
    public string ClaimNo { get; set; } = "";
    public string PolicyNo { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string Email { get; set; } = "";
    public VehicleInfo Vehicle { get; set; } = new();
    public DateOnly? IncidentDate { get; set; }
    public string Handler { get; set; } = "";
    public string Channel { get; set; } = "WhatsApp";
    public string? LastDelivery { get; set; }
    public string Token { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public ClaimStatus Status { get; set; } = ClaimStatus.LinkSent;
    public string Language { get; set; } = "en";
    public DateTimeOffset? ConsentAt { get; set; }
    public GeoPoint? ConsentLocation { get; set; }
    public List<Photo> Photos { get; set; } = [];
    public List<Finding> Findings { get; set; } = [];
    public List<EvidenceCheck> Checks { get; set; } = [];
    public bool Flagged { get; set; }
    public string AiStatus { get; set; } = "pending";   // pending | ok | no-model | error
    public string? AiMessage { get; set; }
    public string? DetectorName { get; set; }
    /// <summary>AI recommendation for the underwriter (never shown to the customer).</summary>
    public AiReview? AiReview { get; set; }
    public string? SignaturePath { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
    public string? ReportHash { get; set; }
    public string? ReportPath { get; set; }
    public string? ErpRef { get; set; }
    public string? ErpError { get; set; }
    public DateTimeOffset? FiledAt { get; set; }
    public bool Reviewed { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }

    public bool LinkUsable(DateTimeOffset now) =>
        now < ExpiresAt && Status is ClaimStatus.LinkSent or ClaimStatus.InSurvey or ClaimStatus.Analysing or ClaimStatus.ReadyToSign;
}

public sealed class VehicleInfo
{
    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    public int Year { get; set; }
    public string Colour { get; set; } = "";
    public string PlateNo { get; set; } = "";
    public string Vin { get; set; } = "";
    public string? Odometer { get; set; }
    public string? PlateRead { get; set; }
    public string? VinRead { get; set; }
}

public sealed record GeoPoint(double Lat, double Lng, double? AccuracyM);

public sealed class Photo
{
    public string Id { get; set; } = "";
    public string Slot { get; set; } = "";
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
    public string ContentType { get; set; } = "image/jpeg";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>Server receive time: the authoritative timestamp (phone clocks can be changed).</summary>
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? CapturedAtClient { get; set; }
    public GeoPoint? Location { get; set; }
    public bool LiveCamera { get; set; }
    public string Quality { get; set; } = "ok";
    public bool QualityOverride { get; set; }
}

public sealed class Finding
{
    public string Type { get; set; } = "";        // Scratch | Minor dent | Dent | Other
    public string Part { get; set; } = "";
    public string Severity { get; set; } = "";    // Low | Medium | High
    public double Confidence { get; set; }
    public string PhotoId { get; set; } = "";
    public string PhotoLabel { get; set; } = "";
    public float[]? Box { get; set; }             // x, y, w, h as fractions of the image
    public bool NeedsReview { get; set; }
    public string? Note { get; set; }
}

public sealed record EvidenceCheck(bool? Ok, string Text);

/// <summary>AI review for UW. Recommendation: Approve | Inspect | Refer.</summary>
public sealed record AiReview(string Recommendation, string Summary, List<string> Reasons, double Confidence);

/// <summary>The photo positions a client must (or may) capture. BR-05: at least the 8 around the car; here 12 are required.</summary>
public static class Slots
{
    public sealed record Slot(string Key, string Label, bool Required);
    public static readonly IReadOnlyList<Slot> All =
    [
        new("front", "Front", true), new("fr", "Front-right corner", true), new("right", "Right side", true),
        new("rr", "Rear-right corner", true), new("rear", "Rear", true), new("rl", "Rear-left corner", true),
        new("left", "Left side", true), new("fl", "Front-left corner", true),
        new("top", "Top view", true),
        new("plate", "Number plate / VIN", true), new("odo", "Odometer", true),
        new("close1", "Damage close-up 1", true), new("close2", "Damage close-up 2", false), new("close3", "Damage close-up 3", false),
    ];
    public static Slot? Find(string key) => All.FirstOrDefault(s => s.Key == key);
    public static int RequiredCount => All.Count(s => s.Required);
}
