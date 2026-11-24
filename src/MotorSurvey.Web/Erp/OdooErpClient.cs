using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MotorSurvey.Core;

namespace MotorSurvey.Web.Erp;

public sealed class OdooOptions
{
    public string Url { get; set; } = "";
    public string Database { get; set; } = "";
    /// <summary>Login of the integration user (needed for the legacy JSON-RPC API only).</summary>
    public string Username { get; set; } = "";
    /// <summary>Odoo API key (Preferences > Account Security > New API Key). Keep it in user-secrets / env vars, not in git.</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>"Json2" for Odoo 19+ (recommended), "JsonRpc" for Odoo 17/18.</summary>
    public string Api { get; set; } = "Json2";
    /// <summary>Odoo model holding motor policies, e.g. a Studio model "x_motor_policy".</summary>
    public string PolicyModel { get; set; } = "x_motor_policy";
    /// <summary>Maps this app's policy fields to Odoo field names on PolicyModel.</summary>
    public Dictionary<string, string> Fields { get; set; } = new();
    /// <summary>Post the survey summary in the record's chatter so the handler sees it.</summary>
    public bool PostChatterNote { get; set; } = true;
}

/// <summary>
/// Odoo connector. Reads policies from PolicyModel, then files the signed report, photos and payload
/// as ir.attachment records on the policy and posts a note in its chatter (BR-11, BR-12).
/// </summary>
public sealed class OdooErpClient(HttpClient http, OdooOptions o, ILogger<OdooErpClient> log) : IErpClient
{
    public string Name => $"Odoo ({o.Url}, {o.Api})";
    private int? _uid;

    private string F(string key) => o.Fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : key;

    public async Task<IReadOnlyList<Policy>> GetPoliciesAsync(CancellationToken ct) =>
        (await SearchReadAsync(o.PolicyModel, new JsonArray(), PolicyFieldNames(), 500, ct)).Select(ToPolicy).ToList();

    public async Task<Policy?> GetPolicyAsync(string policyNo, CancellationToken ct)
    {
        var rows = await SearchReadAsync(o.PolicyModel, Domain(F("PolicyNo"), "=", policyNo), PolicyFieldNames(), 1, ct);
        return rows.Count == 0 ? null : ToPolicy(rows[0]);
    }

    public async Task<ErpUploadResult> UploadSurveyAsync(SurveyPackage pkg, CancellationToken ct)
    {
        try
        {
            var c = pkg.Claim;
            var rec = await SearchReadAsync(o.PolicyModel, Domain(F("PolicyNo"), "=", c.PolicyNo), ["id"], 1, ct);
            if (rec.Count == 0) return new ErpUploadResult(false, null, $"Policy {c.PolicyNo} not found in Odoo model {o.PolicyModel}");
            var resId = rec[0]["id"]!.GetValue<int>();

            var vals = new JsonArray();
            foreach (var f in pkg.Files)
                vals.Add(Attachment($"{c.ClaimNo}_{f.FileName}", f.ContentType, await File.ReadAllBytesAsync(f.LocalPath, ct), resId));
            vals.Add(Attachment($"{c.ClaimNo}_payload.json", "application/json", Encoding.UTF8.GetBytes(pkg.PayloadJson), resId));
            var ids = await CreateAsync("ir.attachment", vals, ct);

            if (o.PostChatterNote)
                await MessagePostAsync(o.PolicyModel, resId, System.Net.WebUtility.HtmlEncode(pkg.SummaryText).Replace("\n", "<br/>"), ids, ct);

            return new ErpUploadResult(true, $"odoo:{o.PolicyModel}/{resId}", null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Odoo upload failed for {Claim}", pkg.Claim.ClaimNo);
            return new ErpUploadResult(false, null, ex.Message);
        }
    }

    // ---------- mapping ----------
    /// <summary>The policy fields this app needs, mapped to Odoo field names in OdooOptions.Fields.</summary>
    public static readonly string[] AppFields =
        ["PolicyNo", "CustomerName", "Mobile", "Email", "Make", "Model", "Year", "Colour", "PlateNo", "VIN", "CoverType", "PolicyStart", "PolicyEnd", "SumInsuredKWD", "UwHandler"];

    private string[] PolicyFieldNames() => AppFields.Select(F).Distinct().ToArray();

    /// <summary>Lists the fields of PolicyModel (name, label, type) so staff can map them on screen.</summary>
    public async Task<List<(string Name, string Label, string Type)>> GetModelFieldsAsync(CancellationToken ct)
    {
        var attrs = new JsonArray("string", "type");
        JsonNode? res = Json2
            ? await Json2Async(o.PolicyModel, "fields_get", new JsonObject { ["attributes"] = attrs }, ct)
            : await ExecuteKwAsync(o.PolicyModel, "fields_get", new JsonArray(), new JsonObject { ["attributes"] = attrs }, ct);
        return (res as JsonObject ?? new JsonObject())
            .Select(kv => (kv.Key, kv.Value?["string"]?.ToString() ?? kv.Key, kv.Value?["type"]?.ToString() ?? ""))
            .OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    private Policy ToPolicy(JsonObject r)
    {
        string S(string key)
        {
            var n = r[F(key)];
            return n switch
            {
                null => "",
                JsonArray a when a.Count > 1 => a[1]?.ToString() ?? "",      // many2one: [id, "Display name"]
                JsonValue v when v.TryGetValue<bool>(out var b) && !b => "",   // Odoo returns false for empty
                _ => n.ToString(),
            };
        }
        return new Policy(S("PolicyNo"), S("CustomerName"), S("Mobile"), S("Email"), S("Make"), S("Model"),
            int.TryParse(S("Year"), out var y) ? y : 0, S("Colour"), S("PlateNo"), S("VIN"), S("CoverType"),
            XlsxReader.ParseDate(S("PolicyStart")), XlsxReader.ParseDate(S("PolicyEnd")),
            decimal.TryParse(S("SumInsuredKWD"), NumberStyles.Any, CultureInfo.InvariantCulture, out var si) ? si : null, S("UwHandler"));
    }

    private static JsonArray Domain(string field, string op, string value) => new() { new JsonArray(field, op, value) };

    private static JsonObject Attachment(string name, string mime, byte[] data, int resId) => new()
    {
        ["name"] = name, ["mimetype"] = mime, ["datas"] = Convert.ToBase64String(data), ["res_id"] = resId,
    };

    // ---------- API calls (JSON-2 for Odoo 19+, JSON-RPC for 17/18) ----------
    private bool Json2 => !string.Equals(o.Api, "JsonRpc", StringComparison.OrdinalIgnoreCase);

    private async Task<List<JsonObject>> SearchReadAsync(string model, JsonArray domain, string[] fields, int limit, CancellationToken ct)
    {
        var fieldsArr = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        JsonNode? res = Json2
            ? await Json2Async(model, "search_read", new JsonObject { ["domain"] = domain, ["fields"] = fieldsArr, ["limit"] = limit }, ct)
            : await ExecuteKwAsync(model, "search_read", new JsonArray(domain), new JsonObject { ["fields"] = fieldsArr, ["limit"] = limit }, ct);
        return res?.AsArray().OfType<JsonObject>().ToList() ?? [];
    }

    private async Task<JsonArray> CreateAsync(string model, JsonArray valsList, CancellationToken ct)
    {
        // attachments are linked to the policy model through res_model
        foreach (var v in valsList.OfType<JsonObject>()) v["res_model"] = o.PolicyModel;
        JsonNode? res = Json2
            ? await Json2Async(model, "create", new JsonObject { ["vals_list"] = valsList }, ct)
            : await ExecuteKwAsync(model, "create", new JsonArray(valsList), new JsonObject(), ct);
        return res as JsonArray ?? new JsonArray(res?.DeepClone());
    }

    private async Task MessagePostAsync(string model, int id, string bodyHtml, JsonArray attachmentIds, CancellationToken ct)
    {
        var kw = new JsonObject { ["body"] = bodyHtml, ["message_type"] = "comment", ["subtype_xmlid"] = "mail.mt_note", ["attachment_ids"] = attachmentIds.DeepClone() };
        if (Json2) { kw["ids"] = new JsonArray(id); await Json2Async(model, "message_post", kw, ct); }
        else await ExecuteKwAsync(model, "message_post", new JsonArray(new JsonArray(id)), kw, ct);
    }

    private async Task<JsonNode?> Json2Async(string model, string method, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.Url.TrimEnd('/')}/json/2/{model}/{method}")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("bearer", o.ApiKey);
        if (!string.IsNullOrEmpty(o.Database)) req.Headers.Add("X-Odoo-Database", o.Database);
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Odoo {model}.{method} failed ({(int)resp.StatusCode}): {Trim(text)}");
        return JsonNode.Parse(text);
    }

    private async Task<JsonNode?> ExecuteKwAsync(string model, string method, JsonArray args, JsonObject kwargs, CancellationToken ct)
    {
        _uid ??= (await RpcAsync("common", "login", new JsonArray(o.Database, o.Username, o.ApiKey), ct))?.GetValue<int>()
                 ?? throw new InvalidOperationException("Odoo login failed");
        return await RpcAsync("object", "execute_kw", new JsonArray(o.Database, _uid, o.ApiKey, model, method, args, kwargs), ct);
    }

    private async Task<JsonNode?> RpcAsync(string service, string method, JsonArray args, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["method"] = "call", ["id"] = Random.Shared.Next(),
            ["params"] = new JsonObject { ["service"] = service, ["method"] = method, ["args"] = args },
        };
        using var resp = await http.PostAsync($"{o.Url.TrimEnd('/')}/jsonrpc", new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (node?["error"] is { } err) throw new InvalidOperationException("Odoo error: " + Trim(err["data"]?["message"]?.ToString() ?? err.ToJsonString()));
        return node?["result"];
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;
}
