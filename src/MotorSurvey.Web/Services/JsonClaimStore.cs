using System.Collections.Concurrent;
using System.Text.Json;
using MotorSurvey.Core;

namespace MotorSurvey.Web.Services;

/// <summary>
/// Pilot store: one JSON file per claim, cached in memory. Fine for hundreds of surveys a day on one server.
/// Swap for a PostgreSQL / SQL Server implementation of IClaimStore when you run several API copies.
/// </summary>
public sealed class JsonClaimStore : IClaimStore
{
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, Claim> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly JsonSerializerOptions _json;

    public JsonClaimStore(string dataFolder, JsonSerializerOptions json)
    {
        _json = json;
        _dir = Path.Combine(dataFolder, "claims");
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try { var c = JsonSerializer.Deserialize<Claim>(File.ReadAllText(f), _json); if (c != null) _cache[c.ClaimNo] = c; }
            catch { /* skip unreadable file; it stays on disk for inspection */ }
        }
    }

    public async Task SaveAsync(Claim claim, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try
        {
            var path = Path.Combine(_dir, Safe(claim.ClaimNo) + ".json");
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(claim, _json), ct);
            File.Move(tmp, path, overwrite: true);
            _cache[claim.ClaimNo] = claim;
        }
        finally { _write.Release(); }
    }

    public Task<Claim?> GetAsync(string claimNo, CancellationToken ct) =>
        Task.FromResult(_cache.TryGetValue(claimNo, out var c) ? c : null);

    public Task<Claim?> FindByTokenAsync(string token, CancellationToken ct) =>
        Task.FromResult(_cache.Values.FirstOrDefault(c => CryptoEquals(c.Token, token)));

    public Task<IReadOnlyList<Claim>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Claim>>(_cache.Values.OrderByDescending(c => c.CreatedAt).ToList());

    internal static string Safe(string s) => string.Concat(s.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
    private static bool CryptoEquals(string a, string b) =>
        a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}

/// <summary>Photos, signatures and reports on local disk. Swap for Azure Blob / MinIO later.</summary>
public sealed class LocalFileStore(string dataFolder) : IFileStore
{
    private readonly string _root = Path.GetFullPath(Path.Combine(dataFolder, "files"));

    public async Task<string> SaveAsync(string claimNo, string fileName, Stream data, CancellationToken ct)
    {
        var rel = Path.Combine(JsonClaimStore.Safe(claimNo), JsonClaimStore.Safe(Path.GetFileNameWithoutExtension(fileName)) + Path.GetExtension(fileName).ToLowerInvariant());
        var full = FullPath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var fs = File.Create(full);
        await data.CopyToAsync(fs, ct);
        return rel;
    }

    public string FullPath(string storedPath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, storedPath));
        if (!full.StartsWith(_root, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid path");
        return full;
    }
}

/// <summary>Used until a trained model is installed: every survey goes to manual UW review.</summary>
public sealed class NoModelDetector : IDamageDetector
{
    public string Name => "No AI model installed";
    public bool IsReady => false;
    public Task<DetectionResult> DetectAsync(string imagePath, Slots.Slot slot, CancellationToken ct) =>
        Task.FromResult(new DetectionResult([], Issue: "No AI model installed"));
}
