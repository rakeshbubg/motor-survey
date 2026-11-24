namespace MotorSurvey.Web.Services;

public sealed class SurveyOptions
{
    /// <summary>Public https address customers open, e.g. https://survey.ilaf.com.kw. Empty = taken from the request.</summary>
    public string PublicBaseUrl { get; set; } = "";
    public int LinkValidHours { get; set; } = 48;
    /// <summary>Findings below this confidence are flagged for UW review (BR-12).</summary>
    public double ReviewConfidence { get; set; } = 0.70;
    /// <summary>Allow the phone's photo picker when the live camera is unavailable. Set false in production (BR-02).</summary>
    public bool AllowFileFallback { get; set; } = true;
    /// <summary>Let customers pick "Upload from gallery" as well as the live camera. Gallery photos are flagged for UW.</summary>
    public bool AllowGallery { get; set; } = true;
    public int MaxPhotoMb { get; set; } = 15;
    /// <summary>Key staff screens must send (header X-Staff-Key). Replace with Azure AD / Odoo login before go-live.</summary>
    public string StaffKey { get; set; } = "";
    public string DataFolder { get; set; } = "data";
}
