# ILAF Motor Self-Survey (.NET 10)

Customer takes live, GPS-stamped photos of their car from a single-use link → AI checks for damage →
customer reviews and signs → report + photos are filed in the ERP (Excel now, Odoo later) and UW is notified.

## Run it on your laptop (5 minutes)

1. Install the **.NET 10 SDK** (https://dotnet.microsoft.com/download) and Visual Studio 2022/2026 or VS Code.
2. Open `MotorSurvey.sln` and run the **MotorSurvey.Web** project. Or from a terminal:
   ```
   cd src/MotorSurvey.Web
   dotnet run
   ```
3. Open **http://localhost:5080**: the staff console (no key needed in Development).
4. Pick a policy (from `data/erp_customers.xlsx`) → **Register claim and generate link** → **Open link**.

> `MotorSurvey.Ai.Onnx` needs NuGet packages; Visual Studio restores them automatically.
> If you only want the app without AI, build just `src/MotorSurvey.Web`.

## Test on a real phone (Android / iPhone)

Phones only allow the **live camera and GPS on https**. Easiest options:
- **Visual Studio Dev Tunnels**: in the run button menu → Dev Tunnels → create a public tunnel, run, and open the tunnel URL.
- **ngrok**: `ngrok http 5080`, then open the `https://…ngrok…` address.

Set `Survey:PublicBaseUrl` to that https address so generated links point to it, then send yourself a link.
On plain http (e.g. `http://192.168.x.x:5080`) the camera falls back to the photo picker, which is allowed only while `AllowFileFallback = true`.

## The ERP (Excel now)

`data/erp_customers.xlsx`, sheet **Policies**: 10 sample customers. Edit or add rows; the app re-reads the file on every request.
Signed surveys are "uploaded" to `data/erp_outbox/<ClaimNo>/` (report, photos, signature, payload.json),
and a row is added to `data/erp_outbox/surveys.csv` (opens in Excel).

## Connect Odoo (ERP connection tab)

Everything is done on screen. Open the staff console and go to **ERP connection**.

1. In Odoo (Studio or a small module), make sure you have a model for motor policies, e.g. `x_motor_policy`, with fields for policy no., customer, mobile, email, make, model, year, colour, plate, VIN, cover, dates, sum insured and UW handler.
2. Create an integration user in Odoo and an **API key** for it (Preferences → Account Security → New API Key).
3. In the app, click the **Odoo** card. It highlights and the settings form opens.
4. Enter the Odoo address, database, API version (Odoo 19+ = JSON-2; Odoo 17/18 = JSON-RPC, which also needs the username), the API key and the policy model.
5. Click **Load fields from Odoo**. The app reads the model's fields and fills in the mapping where the names match. Check each box.
6. Click **Test connection**. You should see "Connected" and your first policies.
7. Click **Save and use Odoo**. The app switches straight away, with no restart. New claims read policies from Odoo, and signed surveys are attached to the policy record with a chatter note.

You can switch back to the Excel card the same way, or upload a new Excel file there.
The API key is stored encrypted in `data/erp_settings.json` (keys in `data/keys/`), and is never shown again on screen. Back up the `data` folder, including `keys`.
Microsoft Dynamics 365 and SAP Business One appear as "Coming soon": add them by writing another `IErpClient` and a card.

## Sending the link by Gmail and SMS

The Claims desk sends each link by **SMS + Email** (default), SMS, Email or WhatsApp, and shows whether it was sent.
Customers open the link on their own phone; the photos, signature and report come back to **UW records** and the ERP outbox.
Put the **customer's real mobile and email** in the Excel file (the sample rows use fake numbers).
Without these settings nothing is sent, and the screen tells you to copy the message yourself.

**Gmail (email)**
1. On the Gmail account you send from, turn on **2-Step Verification** (myaccount.google.com → Security).
2. Create an **App password** (myaccount.google.com/apppasswords), a 16-letter code.
3. In `src/MotorSurvey.Web`:
   ```
   dotnet user-secrets set "Messaging:Email:User" "yourname@gmail.com"
   dotnet user-secrets set "Messaging:Email:Password" "the16letterapppassword"
   ```
Gmail limits a personal account to a few hundred emails a day, which is fine for a demo. Use a company mailbox (Microsoft 365 / Google Workspace SMTP) for the pilot.

**SMS and WhatsApp (Twilio)**
1. Sign up at twilio.com (trial credit is free) and get a phone number. Note the **Account SID** and **Auth Token**.
2. On a trial account, verify each phone number you will send to (Console → Verified Caller IDs). Trial messages start with "Sent from your Twilio trial account".
3. Check that SMS to Kuwait (+965) is enabled under Messaging → Settings → Geo permissions.
4. Set:
   ```
   dotnet user-secrets set "Messaging:Twilio:AccountSid" "AC..."
   dotnet user-secrets set "Messaging:Twilio:AuthToken" "..."
   dotnet user-secrets set "Messaging:Twilio:SmsFrom" "+1XXXXXXXXXX"
   ```
   WhatsApp: join the Twilio WhatsApp sandbox from your phone, then set `Messaging:Twilio:WhatsAppFrom` to the sandbox number.
For the pilot, a Kuwaiti SMS provider usually gives better delivery and a sender name like "ILAF"; only `TwilioAsync` in `Services/Messaging.cs` needs changing.

On the server (docker-compose), put the same values in `deploy/.env`: `GMAIL_USER`, `GMAIL_APP_PASSWORD`, `TWILIO_SID`, `TWILIO_TOKEN`, `TWILIO_SMS_FROM`, `TWILIO_WHATSAPP_FROM`.

## Photo checks on the phone

Every photo (live camera or gallery) is checked on the customer's phone before it is accepted:
- **Is there a car?** For the 8 required positions, a small free model (TensorFlow.js COCO-SSD, Apache 2.0) must see a car
  filling a reasonable part of the frame. Photos of screens, rooms or people are refused with a "retake" message.
- **Quality:** blur, too dark, too bright, low resolution.
- First failure: the customer must **retake**. From the second attempt they may "Use anyway (will be reviewed)",
  and the photo is flagged in the report's evidence checks for UW.
- Gallery uploads are allowed (`Survey:AllowGallery`) but flagged as "not taken live". Set it to `false` to force the live camera.
- Customers can tap a number on the car diagram or the Take button; both offer **Take photo** or **Upload from gallery**.

The model (~5 MB) is downloaded into the server image at build time and cached by the phone. If it can't load, the check is skipped
(never blocks the customer) and UW still sees every photo. It can't tell a real car from a photo of a car on a screen;
the damage model you train later is the place for stronger fraud checks.

## The AI model

Until a model is installed, every survey is routed to manual UW review (and says so).
1. Train RF-DETR on your claim photos: see `training/README.md`.
2. Copy the exported file to `src/MotorSurvey.Web/models/damage.onnx` (optional: `models/parts.onnx`).
3. Build with the AI engine: `dotnet run -p:UseOnnx=true` (or `--build-arg USE_ONNX=true` for Docker).
4. Set `Ai:ClassNames` and `Ai:InputSize` to match your training.

## Settings (`appsettings.json`)

| Setting | Meaning |
|---|---|
| `Survey:PublicBaseUrl` | https address customers open (used in the links) |
| `Survey:LinkValidHours` | link expiry, default 48 (BR-01) |
| `Survey:ReviewConfidence` | findings below this go to UW review, default 0.70 (BR-12) |
| `Survey:AllowFileFallback` | allow the photo picker when the live camera is unavailable. **false in production** (BR-02) |
| `Survey:StaffKey` | key for the staff console. Empty = no key (Development only) |
| `Erp:Provider` | `Excel` or `Odoo` |

## Deploy the pilot (one server)

`deploy/docker-compose.yml` runs the app behind Caddy with automatic HTTPS. Set `SURVEY_DOMAIN`, `STAFF_KEY` (and Odoo values) in a `.env` file next to it, then:
`docker compose -f deploy/docker-compose.yml up -d --build`

## Project layout

```
src/MotorSurvey.Core      models + interfaces (IErpClient, IDamageDetector, IClaimStore, IFileStore, ILinkSender)
src/MotorSurvey.Web       API, client survey page, staff console, Excel + Odoo connectors, report, background AI worker
  wwwroot/survey.html     client survey (English/Arabic, live camera, GPS, signature) — phones and tablets
  wwwroot/index.html      staff console: claims desk + UW records
  data/                   erp_customers.xlsx; claims, photos and outbox are created here at runtime
src/MotorSurvey.Ai.Onnx   RF-DETR ONNX inference (optional, build with -p:UseOnnx=true)
training/                 Python script to train and export the model
deploy/                   Dockerfile + docker-compose with HTTPS
```

## Before go-live (pilot is ready; these are the production steps)

- Replace the staff key with real sign-in (Azure AD / Entra ID or Odoo login).
- Replace `JsonClaimStore` with a PostgreSQL / SQL Server implementation of `IClaimStore` when you run more than one server.
- Replace `LogLinkSender` with your SMS gateway / WhatsApp Business API.
- Set `AllowFileFallback` to false, set a retention period for photos with compliance, and back up `data/`.
- Generate a PDF from `report.html` if UW needs PDF (e.g. Playwright for .NET, Apache 2.0).
