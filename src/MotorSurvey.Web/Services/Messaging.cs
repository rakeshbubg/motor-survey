using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Options;
using MotorSurvey.Core;

namespace MotorSurvey.Web.Services;

public sealed class MessagingOptions
{
    public EmailOptions Email { get; set; } = new();
    public TwilioOptions Twilio { get; set; } = new();
}

/// <summary>Gmail: smtp.gmail.com, port 587, your Gmail address and a Google "App password" (needs 2-Step Verification).</summary>
public sealed class EmailOptions
{
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromName { get; set; } = "ILAF Takaful Claims";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Password);
}

/// <summary>Twilio SMS and WhatsApp. Trial accounts can only send to numbers verified in the Twilio console.</summary>
public sealed class TwilioOptions
{
    public string AccountSid { get; set; } = "";
    public string AuthToken { get; set; } = "";
    /// <summary>Your Twilio phone number or approved sender ID, e.g. +15551234567.</summary>
    public string SmsFrom { get; set; } = "";
    /// <summary>WhatsApp sender, e.g. +14155238886 for the Twilio sandbox.</summary>
    public string WhatsAppFrom { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.twilio.com";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccountSid) && !string.IsNullOrWhiteSpace(AuthToken);
}

/// <summary>Sends the survey link by Email (Gmail SMTP), SMS or WhatsApp (Twilio), or "SMS + Email".</summary>
public sealed class LinkSender(IOptions<MessagingOptions> opt, IHttpClientFactory http, ILogger<LinkSender> log) : ILinkSender
{
    private readonly MessagingOptions _o = opt.Value;

    public async Task<string> SendAsync(Claim c, string url, string message, CancellationToken ct)
    {
        var ch = c.Channel.ToLowerInvariant();
        var notes = new List<string>();
        if (ch.Contains("email")) notes.Add(await EmailAsync(c, url, ct));
        if (ch.Contains("sms")) notes.Add(await TwilioAsync(c.Mobile, _o.Twilio.SmsFrom, message, "SMS", ct));
        if (ch.Contains("whatsapp")) notes.Add(await TwilioAsync("whatsapp:" + c.Mobile, string.IsNullOrWhiteSpace(_o.Twilio.WhatsAppFrom) ? "" : "whatsapp:" + _o.Twilio.WhatsAppFrom, message, "WhatsApp", ct));
        if (notes.Count == 0) notes.Add($"Unknown channel '{c.Channel}'");
        var result = string.Join(" · ", notes);
        log.LogInformation("Link for {Claim}: {Result}", c.ClaimNo, result);
        return result;
    }

    private async Task<string> EmailAsync(Claim c, string url, CancellationToken ct)
    {
        var e = _o.Email;
        if (!e.IsConfigured) return "Email not configured — copy the message and send it yourself";
        if (string.IsNullOrWhiteSpace(c.Email)) return "Email: no address on the policy";
        try
        {
            using var mail = new MailMessage
            {
                From = new MailAddress(e.User, e.FromName),
                Subject = $"Vehicle survey for claim {c.ClaimNo} | معاينة المركبة للمطالبة {c.ClaimNo}",
                Body = EmailHtml(c, url), IsBodyHtml = true, BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8,
            };
            mail.To.Add(new MailAddress(c.Email, c.CustomerName));
            using var smtp = new SmtpClient(e.Host, e.Port) { EnableSsl = e.UseSsl, Credentials = new NetworkCredential(e.User, e.Password), Timeout = 30000 };
            await smtp.SendMailAsync(mail, ct);
            return $"Email sent to {c.Email}";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Email failed for {Claim}", c.ClaimNo);
            return "Email failed: " + Short(ex.GetBaseException().Message);
        }
    }

    private async Task<string> TwilioAsync(string to, string from, string body, string label, CancellationToken ct)
    {
        var t = _o.Twilio;
        if (!t.IsConfigured || string.IsNullOrWhiteSpace(from)) return $"{label} not configured — copy the message and send it yourself";
        if (to.EndsWith(':') || string.IsNullOrWhiteSpace(to)) return $"{label}: no mobile number on the policy";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{t.BaseUrl.TrimEnd('/')}/2010-04-01/Accounts/{Uri.EscapeDataString(t.AccountSid)}/Messages.json")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["To"] = to, ["From"] = from, ["Body"] = body }),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{t.AccountSid}:{t.AuthToken}")));
            using var resp = await http.CreateClient("twilio").SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return $"{label} sent to {to.Replace("whatsapp:", "")}";
            var msg = System.Text.Json.Nodes.JsonNode.Parse(text)?["message"]?.ToString() ?? text;
            return $"{label} failed: {Short(msg)}";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Label} failed", label);
            return $"{label} failed: {Short(ex.Message)}";
        }
    }

    private static string Short(string s) => s.Length > 160 ? s[..160] + "…" : s;

    private static string EmailHtml(Claim c, string url)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var until = c.ExpiresAt.ToOffset(TimeSpan.FromHours(3)).ToString("dd MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var btn = $"<a href=\"{E(url)}\" style=\"display:inline-block;background:#111;color:#fff;text-decoration:none;padding:14px 22px;border-radius:8px;font-weight:bold\">";
        return $"""
<div style="font-family:Arial,Helvetica,sans-serif;max-width:560px;margin:auto;color:#111;line-height:1.5">
<h2 style="margin:0 0 12px">ILAF Takaful — vehicle survey</h2>
<p>Dear {E(c.CustomerName)},</p>
<p>Please complete the vehicle survey for claim <b>{E(c.ClaimNo)}</b> ({E(c.Vehicle.Make)} {E(c.Vehicle.Model)}, plate {E(c.Vehicle.PlateNo)}). Open the link on your phone and take the photos around your car. It takes about 5 minutes.</p>
<p>{btn}Start the survey</a></p>
<p style="color:#555;font-size:13px">The link works once and is valid until {until} (Kuwait time).</p>
<hr style="border:0;border-top:1px solid #ddd;margin:22px 0">
<div dir="rtl" style="text-align:right">
<p>عزيزي/عزيزتي {E(c.CustomerName)}،</p>
<p>يرجى إكمال معاينة المركبة للمطالبة رقم <b>{E(c.ClaimNo)}</b>. افتح الرابط على هاتفك والتقط الصور حول مركبتك. يستغرق ذلك حوالي 5 دقائق.</p>
<p>{btn}ابدأ المعاينة</a></p>
<p style="color:#555;font-size:13px">يعمل الرابط مرة واحدة وهو صالح حتى {until} (بتوقيت الكويت).</p>
</div>
<p style="color:#888;font-size:12px;margin-top:24px">If the button does not work, copy this link: {E(url)}</p>
</div>
""";
    }
}
