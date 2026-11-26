using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MotorSurvey.Core;

namespace MotorSurvey.Web.Services;

public sealed class GeminiOptions
{
    /// <summary>Key from Google AI Studio (aistudio.google.com → Get API key). Keep it in deploy/.env, never in git.</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>Any Gemini model that accepts images. Free-tier models change over time: check ai.google.dev/gemini-api/docs/pricing.</summary>
    public string Model { get; set; } = "gemini-3.5-flash";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com";
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Damage check with Google Gemini (vision). One request per survey with all photos.
/// Free tier: Google may use the content to improve its products — use test cars for demos, the paid tier or your own model for real customers.
/// </summary>
public sealed class GeminiDamageDetector(HttpClient http, GeminiOptions o, ILogger<GeminiDamageDetector> log) : IBatchDamageDetector
{
    public string Name => $"Gemini ({o.Model})";
    public bool IsReady => !string.IsNullOrWhiteSpace(o.ApiKey);

    public async Task<DetectionResult> DetectAsync(string imagePath, Slots.Slot slot, CancellationToken ct) =>
        (await DetectAllAsync([(imagePath, slot)], new SurveyContext("", "", 0, "", "", null), ct)).Photos[0];

    public async Task<BatchDetection> DetectAllAsync(IReadOnlyList<(string ImagePath, Slots.Slot Slot)> photos, SurveyContext ctx, CancellationToken ct)
    {
        var parts = new JsonArray { new JsonObject { ["text"] = Prompt(photos, ctx) } };
        for (int i = 0; i < photos.Count; i++)
        {
            parts.Add(new JsonObject { ["text"] = $"Photo {i + 1}: {photos[i].Slot.Label}" });
            parts.Add(new JsonObject
            {
                ["inline_data"] = new JsonObject { ["mime_type"] = Mime(photos[i].ImagePath), ["data"] = Convert.ToBase64String(await File.ReadAllBytesAsync(photos[i].ImagePath, ct)) },
            });
        }
        var body = new JsonObject
        {
            ["contents"] = new JsonArray { new JsonObject { ["role"] = "user", ["parts"] = parts } },
            ["generationConfig"] = new JsonObject { ["responseMimeType"] = "application/json", ["temperature"] = 0.1 },
        };

        var text = await CallAsync(body.ToJsonString(), ct);
        var root = JsonNode.Parse(StripFence(text)) ?? throw new InvalidOperationException("Empty AI reply");
        var arr = (root as JsonObject)?["photos"] as JsonArray ?? root as JsonArray ?? throw new InvalidOperationException("AI reply had no photos list");

        var results = new List<DetectionResult>();
        for (int i = 0; i < photos.Count; i++)
        {
            var p = arr.OfType<JsonObject>().FirstOrDefault(x => Int(x["n"]) == i + 1) ?? (i < arr.Count ? arr[i] as JsonObject : null);
            if (p is null) { results.Add(new DetectionResult([], Issue: "not analysed")); continue; }
            var dmg = (p["findings"] as JsonArray ?? []).OfType<JsonObject>().Select(f =>
            {
                var box = f["box"] as JsonArray;
                float B(int k) => box is { Count: 4 } ? Math.Clamp(Float(box[k]), 0, 1) : 0;
                var type = Str(f["type"]) switch { "Scratch" => "Scratch", "Minor dent" => "Minor dent", "Dent" => "Dent", _ => "Other" };
                return new DetectedDamage(type, Str(f["part"]) is { Length: > 0 } part ? part : photos[i].Slot.Label,
                    Math.Clamp(Float(f["confidence"]), 0, 1), B(0), B(1), B(2), B(3), Str(f["severity"]), Str(f["note"]));
            }).ToList();
            results.Add(new DetectionResult(dmg, Null(Str(p["plate"])), Null(Str(p["vin"])), Null(Str(p["odometer"])), Null(Str(p["issue"]))));
        }

        AiReview? review = null;
        if ((root as JsonObject)?["review"] is JsonObject rv)
        {
            var rec = Str(rv["recommendation"]) switch
            {
                var x when x.Equals("Approve", StringComparison.OrdinalIgnoreCase) => "Approve",
                var x when x.Equals("Refer", StringComparison.OrdinalIgnoreCase) => "Refer",
                _ => "Inspect",
            };
            var reasons = (rv["reasons"] as JsonArray ?? []).Select(Str).Where(x => x.Length > 0).Take(8).ToList();
            review = new AiReview(rec, Str(rv["summary"]), reasons, Math.Round(Math.Clamp(Float(rv["confidence"]), 0, 1), 2));
        }
        return new BatchDetection(results, review);
    }

    private async Task<string> CallAsync(string json, CancellationToken ct)
    {
        var url = $"{o.BaseUrl.TrimEnd('/')}/v1beta/models/{Uri.EscapeDataString(o.Model)}:generateContent";
        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.Add("x-goog-api-key", o.ApiKey);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
            using var resp = await http.SendAsync(req, cts.Token);
            var raw = await resp.Content.ReadAsStringAsync(cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                var n = JsonNode.Parse(raw);
                var t = n?["candidates"]?[0]?["content"]?["parts"]?.AsArray().Select(x => x?["text"]?.ToString()).FirstOrDefault(x => !string.IsNullOrEmpty(x));
                return t ?? throw new InvalidOperationException("AI returned no text (" + (n?["promptFeedback"]?["blockReason"]?.ToString() ?? n?["candidates"]?[0]?["finishReason"]?.ToString() ?? "unknown") + ")");
            }
            // free tier: 429 = per-minute limit; wait and retry a couple of times
            if ((resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500) && attempt < 3)
            {
                log.LogWarning("Gemini {Status}, retrying", (int)resp.StatusCode);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 20), ct);
                continue;
            }
            var msg = JsonNode.Parse(raw)?["error"]?["message"]?.ToString() ?? raw;
            throw new InvalidOperationException($"Gemini {(int)resp.StatusCode}: {(msg.Length > 200 ? msg[..200] : msg)}");
        }
    }

    private static string Prompt(IReadOnlyList<(string ImagePath, Slots.Slot Slot)> photos, SurveyContext c) => $$"""
You are a motor-insurance damage surveyor in Kuwait. You get {{photos.Count}} photos of ONE insured vehicle, each labelled with its capture position.
Insured vehicle on the policy: {{c.Make}} {{c.Model}} {{(c.Year > 0 ? c.Year.ToString(CultureInfo.InvariantCulture) : "")}}, colour {{c.Colour}}, plate {{c.PlateNo}}. Incident date: {{c.IncidentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown"}}.
For every photo:
- List each visible exterior fault. "type" must be one of: "Scratch", "Minor dent", "Dent", "Other" (Other = cracked or broken lamp, glass crack, paint chip, bumper misalignment, tyre damage, missing part; say what in "note").
- "part": the vehicle part, e.g. "Front bumper, left side", "Front-left door".
- "severity": "Low" (cosmetic, polish or touch-up), "Medium" (panel repair and paint), "High" (part replacement likely).
- "confidence": 0 to 1. Only report what you can actually see; normal reflections, dirt and shadows are not damage.
- "box": [x, y, w, h] around the damage as fractions (0-1) of the image width/height.
- Read "plate", "vin" and "odometer" if they are visible, else "".
- "issue": "" if fine. Otherwise a short reason: photo is of a screen or a printout, no vehicle visible, wrong position for its label, too dark or blurry to judge, or it looks like a different vehicle from the other photos.
Then write a "review" for the underwriter (the customer never sees it):
- "recommendation": "Approve" if the photos are genuine, consistent with the insured vehicle and the damage is clear;
  "Inspect" if something needs a closer look or a physical survey (unclear photos, heavy or hidden damage, missing angles);
  "Refer" if there are signs of possible fraud (a different vehicle or colour/make/plate not matching the policy, photos of screens or printouts,
  the same damage shown as different positions, old rust or weathering suggesting damage from before the incident).
- "summary": 2-3 plain sentences for the underwriter.
- "reasons": short bullet reasons for the recommendation. "confidence": 0 to 1.
Do not estimate repair costs.
Reply with only JSON:
{"photos":[{"n":1,"findings":[{"type":"Scratch","part":"Front bumper, left side","severity":"Low","confidence":0.82,"box":[0.1,0.5,0.2,0.1],"note":""}],"plate":"","vin":"","odometer":"","issue":""}],
 "review":{"recommendation":"Approve","summary":"","reasons":[""],"confidence":0.8} }
""";

    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
    private static string StripFence(string t) { t = t.Trim(); if (t.StartsWith("```")) { var a = t.IndexOf('\n'); var b = t.LastIndexOf("```", StringComparison.Ordinal); if (a > 0 && b > a) t = t[(a + 1)..b]; } return t; }
    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : n?.ToString().Trim() ?? "";
    private static string? Null(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static float Float(JsonNode? n) => n is JsonValue v && (v.TryGetValue<double>(out var d) || double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) ? (float)d : 0;
    private static int Int(JsonNode? n) => (int)Math.Round(Float(n));
}
