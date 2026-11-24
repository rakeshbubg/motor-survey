using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MotorSurvey.Core;
using SkiaSharp;

namespace MotorSurvey.Ai.Onnx;

public sealed class OnnxDetectorOptions
{
    public string ModelPath { get; set; } = "models/damage.onnx";
    /// <summary>Square input size the model was exported with (RF-DETR Medium: 576; Seg Medium: 432).</summary>
    public int InputSize { get; set; } = 576;
    public float ScoreThreshold { get; set; } = 0.35f;
    /// <summary>Class names in the order used during training. Anything not Scratch / Minor dent / Dent is reported as "Other".</summary>
    public string[] ClassNames { get; set; } = ["Scratch", "Minor dent", "Dent"];
    /// <summary>Optional second model that finds car parts, so findings say "Front-left door" instead of the photo position.</summary>
    public string? PartsModelPath { get; set; }
    public string[] PartClassNames { get; set; } = [];
    public int Threads { get; set; } = 0;
}

/// <summary>
/// Runs an RF-DETR model exported to ONNX (inputs: "input" [1,3,S,S]; outputs: boxes "dets" [1,Q,4] as cx,cy,w,h 0-1
/// and class logits "labels" [1,Q,C]). Train and export in Python (see /training), then drop the .onnx file in models/.
/// </summary>
public sealed class OnnxDamageDetector : IDamageDetector, IDisposable
{
    private static readonly HashSet<string> Known = ["Scratch", "Minor dent", "Dent"];
    private readonly OnnxDetectorOptions _o;
    private readonly InferenceSession _damage;
    private readonly InferenceSession? _parts;

    public OnnxDamageDetector(OnnxDetectorOptions o)
    {
        _o = o;
        var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (o.Threads > 0) so.IntraOpNumThreads = o.Threads;
        // On Intel CPUs, the Intel.ML.OnnxRuntime.OpenVino package + so.AppendExecutionProvider_OpenVINO() is noticeably faster.
        _damage = new InferenceSession(o.ModelPath, so);
        if (!string.IsNullOrWhiteSpace(o.PartsModelPath) && File.Exists(o.PartsModelPath))
            _parts = new InferenceSession(o.PartsModelPath, so);
    }

    public string Name => "RF-DETR (ONNX) " + Path.GetFileName(_o.ModelPath) + (_parts != null ? " + parts" : "");
    public bool IsReady => true;

    public Task<DetectionResult> DetectAsync(string imagePath, Slots.Slot slot, CancellationToken ct) => Task.Run(() =>
    {
        using var bmp = SKBitmap.Decode(imagePath);
        if (bmp == null) return new DetectionResult([], Issue: $"{slot.Label}: photo could not be read");
        var input = ToTensor(bmp, _o.InputSize);
        var damages = Run(_damage, input, _o.ClassNames, _o.ScoreThreshold);
        var parts = _parts != null ? Run(_parts, input, _o.PartClassNames, 0.4f) : [];

        var list = damages.Select(d =>
        {
            var part = parts.Where(p => Overlap(d, p) > 0.3f).OrderByDescending(p => Overlap(d, p) * p.Score).FirstOrDefault();
            var type = Known.Contains(d.Name) ? d.Name : "Other";
            var label = type == "Other" ? $"{d.Name} — {part?.Name ?? slot.Label}" : part?.Name ?? slot.Label;
            return new DetectedDamage(type, label, d.Score, d.X, d.Y, d.W, d.H);
        }).ToList();
        return new DetectionResult(list);
    }, ct);

    private sealed record Box(string Name, float Score, float X, float Y, float W, float H);

    private static DenseTensor<float> ToTensor(SKBitmap src, int size)
    {
        using var resized = src.Resize(new SKImageInfo(size, size, SKColorType.Rgba8888), new SKSamplingOptions(SKFilterMode.Linear));
        var t = new DenseTensor<float>([1, 3, size, size]);
        float[] mean = [0.485f, 0.456f, 0.406f], std = [0.229f, 0.224f, 0.225f];
        var px = resized.Pixels;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var c = px[y * size + x];
                t[0, 0, y, x] = (c.Red / 255f - mean[0]) / std[0];
                t[0, 1, y, x] = (c.Green / 255f - mean[1]) / std[1];
                t[0, 2, y, x] = (c.Blue / 255f - mean[2]) / std[2];
            }
        return t;
    }

    private static List<Box> Run(InferenceSession s, DenseTensor<float> input, string[] names, float threshold)
    {
        using var res = s.Run([NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(), input)]);
        var dets = res.First(r => r.Name == "dets").AsTensor<float>();      // [1,Q,4]  cx,cy,w,h (0-1)
        var logits = res.First(r => r.Name == "labels").AsTensor<float>(); // [1,Q,C]
        int q = dets.Dimensions[1], cls = logits.Dimensions[2];
        var boxes = new List<Box>();
        for (int i = 0; i < q; i++)
        {
            int best = 0; float bestScore = 0;
            for (int k = 0; k < cls; k++) { var p = 1f / (1f + MathF.Exp(-logits[0, i, k])); if (p > bestScore) { bestScore = p; best = k; } }
            if (bestScore < threshold) continue;
            // RF-DETR may reserve class 0 as background when exported from a COCO-format dataset: shift if your export does.
            var name = best < names.Length ? names[best] : $"class {best}";
            float cx = dets[0, i, 0], cy = dets[0, i, 1], w = dets[0, i, 2], h = dets[0, i, 3];
            boxes.Add(new Box(name, bestScore, Math.Clamp(cx - w / 2, 0, 1), Math.Clamp(cy - h / 2, 0, 1), w, h));
        }
        return Nms(boxes, 0.5f);
    }

    private static List<Box> Nms(List<Box> boxes, float iou)
    {
        var keep = new List<Box>();
        foreach (var b in boxes.OrderByDescending(b => b.Score))
            if (keep.All(k => k.Name != b.Name || IoU(k, b) < iou)) keep.Add(b);
        return keep;
    }

    private static float Inter(Box a, Box b) =>
        Math.Max(0, Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y));
    private static float IoU(Box a, Box b) { var i = Inter(a, b); return i / (a.W * a.H + b.W * b.H - i + 1e-6f); }
    /// <summary>Share of the damage box that lies inside the part box.</summary>
    private static float Overlap(Box damage, Box part) => Inter(damage, part) / (damage.W * damage.H + 1e-6f);

    public void Dispose() { _damage.Dispose(); _parts?.Dispose(); }
}
