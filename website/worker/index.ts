/** Cloudflare Worker entry point for the vinext-starter template. */
import { handleImageOptimization, DEFAULT_DEVICE_SIZES, DEFAULT_IMAGE_SIZES } from "vinext/server/image-optimization";
import handler from "vinext/server/app-router-entry";

interface Env {
  ASSETS: Fetcher;
  RESEND_API_KEY?: string;
  BETA_EMAIL_ADMIN_TOKEN?: string;
  BETA_EMAIL_BCC?: string;
  COMPANION_MAC_DOWNLOAD_URL?: string;
  COMPANION_WINDOWS_DOWNLOAD_URL?: string;
  DB: D1Database;
  IMAGES: {
    input(stream: ReadableStream): {
      transform(options: Record<string, unknown>): {
        output(options: { format: string; quality: number }): Promise<{ response(): Response }>;
      };
    };
  };
}

async function sha256(value: string): Promise<string> {
  const bytes = new TextEncoder().encode(value);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
}

async function sendEmail(apiKey: string, message: Record<string, unknown>): Promise<boolean> {
  const response = await fetch("https://api.resend.com/emails", {
    method: "POST",
    headers: { Authorization: `Bearer ${apiKey}`, "Content-Type": "application/json" },
    body: JSON.stringify(message),
  });
  return response.ok;
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>'"]/g, character => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;",
  }[character] ?? character));
}

function emailShell(content: string, preheader: string): string {
  return `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1.0">
<title>AudioChoice</title></head>
<body style="margin:0;padding:0;background:#071008;color:#edf2ed;font-family:Arial,Helvetica,sans-serif;">
  <span style="display:none!important;visibility:hidden;opacity:0;color:transparent;height:0;width:0;overflow:hidden;">${preheader}</span>
  <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="background:#071008;padding:34px 12px;"><tr><td align="center">
    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="max-width:620px;background:#0c160e;border:1px solid #304133;border-radius:24px;overflow:hidden;">
      <tr><td style="padding:30px 34px 26px;background:linear-gradient(135deg,#102014,#071008);border-bottom:1px solid #304133;">
        <div style="font-size:30px;font-weight:800;letter-spacing:-1.2px;color:#f4f7f4;">Audio<span style="color:#82ec27;">Choice</span></div>
        <div style="margin-top:9px;color:#82ec27;font-size:11px;font-weight:800;letter-spacing:2px;">LISTEN YOUR WAY</div>
      </td></tr>
      <tr><td style="padding:34px;color:#d8e0d8;font-size:16px;line-height:1.65;">${content}</td></tr>
      <tr><td style="padding:21px 34px;background:#081009;border-top:1px solid #263529;color:#8fa08f;font-size:12px;line-height:1.5;">AudioChoice &middot; <a href="mailto:support@audiochoiceapp.com" style="color:#9af04b;text-decoration:none;">support@audiochoiceapp.com</a></td></tr>
    </table>
  </td></tr></table>
</body></html>`;
}

const companionInstructions = `If you downloaded an AAX audiobook from Audible, visit https://audiochoiceapp.com/companion on your computer to transfer it to your phone. The same transfer page also works for M4B, M4A, and MP3 audiobook files already on your computer. If the audiobook files are already on your phone, use Import directly in the AudioChoice app.`;

function betaWelcomeEmail(name: string, platform: string) {
  const text = `Hi ${name},

Thank you for joining the AudioChoice ${platform} beta. We appreciate your help testing the app and improving filter accuracy.

${companionInstructions}

You will need your own legitimate copy of the audiobook used for this beta. Please keep the original file on your computer until the transfer or import has completed successfully.

Questions? Reply to this email or contact support@audiochoiceapp.com.

AudioChoice
Listen Your Way.`;
  const safeName = escapeHtml(name);
  const safeInstructions = escapeHtml(companionInstructions).replace(/(https:\/\/[^\s]+)/g, '<a href="$1" style="color:#9af04b;text-decoration:none;">$1</a>');
  const html = emailShell(`<p style="margin:0 0 20px;">Hi ${safeName},</p><p style="margin:0 0 20px;">Thank you for joining the AudioChoice ${escapeHtml(platform)} beta. We appreciate your help testing the app and improving filter accuracy.</p><div style="margin:22px 0;padding:17px 19px;border-radius:13px;background:#142214;border:1px solid #304133;"><strong style="color:#f4f7f4;">Moving your audiobook to your phone</strong><p style="margin:9px 0 0;">${safeInstructions}</p></div><p style="margin:0 0 20px;">You will need your own legitimate copy of the audiobook used for this beta. Please keep the original file on your computer until the transfer or import has completed successfully.</p><p style="margin:0;">Questions? Reply to this email or contact <a href="mailto:support@audiochoiceapp.com" style="color:#9af04b;text-decoration:none;">support@audiochoiceapp.com</a>.</p>`, `AudioChoice beta instructions for ${name}`);
  return { text, html };
}

type BetaEmailRecipient = { firstName: string; email: string };

function parseBetaRecipients(value: unknown): { recipients?: BetaEmailRecipient[]; error?: string } {
  if (typeof value !== "string") return { error: "Paste one First Name,Email row per line." };
  const rows = value.split(/\r?\n/).map(row => row.trim()).filter(Boolean);
  const recipients: BetaEmailRecipient[] = [];
  for (const row of rows) {
    const fields = row.split(",").map(field => field.trim().replace(/^\"|\"$/g, ""));
    if (fields.length < 2 || /^first\s*name$/i.test(fields[0]) || /^email$/i.test(fields[1])) continue;
    const firstName = fields[0].slice(0, 80);
    const email = fields.slice(1).join(",").toLowerCase();
    if (!firstName || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) || email.length > 254) return { error: `Check this row: ${row}` };
    if (!recipients.some(item => item.email === email)) recipients.push({ firstName, email });
  }
  if (!recipients.length) return { error: "No recipients found. Use First Name,Email rows." };
  if (recipients.length > 100) return { error: "Send at most 100 beta emails at a time." };
  return { recipients };
}

interface ExecutionContext {
  waitUntil(promise: Promise<unknown>): void;
  passThroughOnException(): void;
}

// Image security config. SVG sources with .svg extension auto-skip the
// optimization endpoint on the client side (served directly, no proxy).
// To route SVGs through the optimizer (with security headers), set
// dangerouslyAllowSVG: true in next.config.js and uncomment below:
// const imageConfig: ImageConfig = { dangerouslyAllowSVG: true };

const worker = {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);

    if (request.method === "GET" && url.pathname === "/api/companion-download/macos") {
      if (!env.COMPANION_MAC_DOWNLOAD_URL) return new Response("Download temporarily unavailable.", { status: 503 });
      return Response.redirect(env.COMPANION_MAC_DOWNLOAD_URL, 302);
    }

    if (request.method === "GET" && url.pathname === "/api/companion-download/windows") {
      if (!env.COMPANION_WINDOWS_DOWNLOAD_URL) return new Response("Download temporarily unavailable.", { status: 503 });
      return Response.redirect(env.COMPANION_WINDOWS_DOWNLOAD_URL, 302);
    }

    if (url.pathname === "/api/updates" && request.method === "POST") {
      if (!env.RESEND_API_KEY) {
        return Response.json({ error: "Email service unavailable" }, { status: 503 });
      }

      let body: { email?: unknown; website?: unknown };
      try {
        body = await request.json() as { email?: unknown; website?: unknown };
      } catch {
        return Response.json({ error: "Invalid request" }, { status: 400 });
      }

      if (body.website) return Response.json({ ok: true });
      const email = typeof body.email === "string" ? body.email.trim().toLowerCase() : "";
      if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) || email.length > 254) {
        return Response.json({ error: "Enter a valid email" }, { status: 400 });
      }

      const resendResponse = await fetch("https://api.resend.com/emails", {
        method: "POST",
        headers: {
          Authorization: `Bearer ${env.RESEND_API_KEY}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          from: "AudioChoice Updates <updates@audiochoiceapp.com>",
          to: ["support@audiochoiceapp.com"],
          reply_to: email,
          subject: "New AudioChoice launch update signup",
          text: `A visitor requested AudioChoice launch updates.\n\nEmail: ${email}\nSubmitted: ${new Date().toISOString()}`,
        }),
      });

      if (!resendResponse.ok) {
        return Response.json({ error: "Email delivery failed" }, { status: 502 });
      }

      return Response.json({ ok: true });
    }

    if (url.pathname === "/api/android-beta" && request.method === "POST") {
      if (!env.RESEND_API_KEY || !env.DB) {
        return Response.json({ error: "Beta signup is temporarily unavailable" }, { status: 503 });
      }

      let body: Record<string, unknown>;
      try {
        body = await request.json() as Record<string, unknown>;
      } catch {
        return Response.json({ error: "Invalid request" }, { status: 400 });
      }
      if (body.website) return Response.json({ ok: true });

      const clean = (value: unknown, maximum: number) => typeof value === "string" ? value.trim().slice(0, maximum) : "";
      const email = clean(body.email, 254).toLowerCase();
      const name = clean(body.name, 100);
      const ownershipSource = clean(body.ownershipSource, 120);
      const audiobooksPerMonth = clean(body.audiobooksPerMonth, 40);
      const platform = clean(body.platform, 20);
      const consentAccepted = body.consent === "accepted";
      if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) || !name || !ownershipSource || !audiobooksPerMonth || !["iOS", "Android"].includes(platform) || !consentAccepted) {
        return Response.json({ error: "Complete every field with valid information" }, { status: 400 });
      }

      const now = new Date().toISOString();
      const hourStart = new Date(Math.floor(Date.now() / 3_600_000) * 3_600_000).toISOString();
      const clientAddress = request.headers.get("cf-connecting-ip") || "unknown";
      const clientHash = await sha256(`audiochoice-beta:${clientAddress}`);
      await env.DB.batch([
        env.DB.prepare(`CREATE TABLE IF NOT EXISTS beta_applicants (
          id TEXT PRIMARY KEY,
          email TEXT NOT NULL UNIQUE,
          name TEXT NOT NULL,
          ownership_source TEXT NOT NULL,
          audiobooks_per_month TEXT NOT NULL,
          group_name TEXT NOT NULL DEFAULT 'android_beta',
          consented_at TEXT NOT NULL,
          created_at TEXT NOT NULL,
          updated_at TEXT NOT NULL
        )`),
        env.DB.prepare("CREATE INDEX IF NOT EXISTS beta_applicants_group_idx ON beta_applicants(group_name)"),
        env.DB.prepare(`CREATE TABLE IF NOT EXISTS beta_submission_limits (
          client_hash TEXT NOT NULL,
          window_start TEXT NOT NULL,
          attempts INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (client_hash, window_start)
        )`),
      ]);

      await env.DB.prepare(`INSERT INTO beta_submission_limits (client_hash, window_start, attempts)
        VALUES (?, ?, 1)
        ON CONFLICT(client_hash, window_start) DO UPDATE SET attempts = attempts + 1`)
        .bind(clientHash, hourStart)
        .run();
      const rate = await env.DB.prepare("SELECT attempts FROM beta_submission_limits WHERE client_hash = ? AND window_start = ?")
        .bind(clientHash, hourStart)
        .first<{ attempts: number }>();
      if ((rate?.attempts ?? 0) > 5) {
        return Response.json({ error: "Too many applications were submitted. Please try again later." }, { status: 429 });
      }
      ctx.waitUntil(env.DB.prepare("DELETE FROM beta_submission_limits WHERE window_start < ?")
        .bind(new Date(Date.now() - 172_800_000).toISOString())
        .run());

      const groupName = platform === "iOS" ? "ios_beta" : "android_beta";
      await env.DB.prepare(`INSERT INTO beta_applicants (
        id, email, name, ownership_source, audiobooks_per_month, group_name, consented_at, created_at, updated_at
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(email) DO UPDATE SET
        name = excluded.name,
        ownership_source = excluded.ownership_source,
        audiobooks_per_month = excluded.audiobooks_per_month,
        group_name = excluded.group_name,
        consented_at = excluded.consented_at,
        updated_at = excluded.updated_at`)
        .bind(crypto.randomUUID(), email, name, ownershipSource, audiobooksPerMonth, groupName, now, now, now)
        .run();

      const welcome = betaWelcomeEmail(name, platform);
      const [adminNotified, applicantNotified] = await Promise.all([
        sendEmail(env.RESEND_API_KEY, {
          from: "AudioChoice Beta <updates@audiochoiceapp.com>",
          to: ["support@audiochoiceapp.com"],
          reply_to: email,
          subject: `${platform} beta application — ${name}`,
          text: `A new listener applied for the AudioChoice ${platform} beta.\n\nName: ${name}\nEmail: ${email}\nPlatform: ${platform}\nAudiobook owned through: ${ownershipSource}\nAudiobooks per month: ${audiobooksPerMonth}\nGroup: ${platform} Beta\nConsent recorded: ${now}\nSubmitted: ${now}`,
        }),
        sendEmail(env.RESEND_API_KEY, {
          from: "AudioChoice Beta <updates@audiochoiceapp.com>",
          to: [email],
          reply_to: "support@audiochoiceapp.com",
          subject: `Thank you for your interest in the AudioChoice ${platform} beta`,
          text: welcome.text,
          html: welcome.html,
        }),
      ]);
      if (!adminNotified) console.error("Android beta admin notification delivery failed");
      if (!applicantNotified) console.error("Android beta applicant confirmation delivery failed");
      return Response.json({ ok: true });
    }

    if (url.pathname === "/api/beta-welcome" && request.method === "POST") {
      if (!env.RESEND_API_KEY || !env.BETA_EMAIL_ADMIN_TOKEN) return Response.json({ error: "The beta email tool is not configured yet." }, { status: 503 });
      if (request.headers.get("authorization") !== `Bearer ${env.BETA_EMAIL_ADMIN_TOKEN}`) return Response.json({ error: "Not authorized." }, { status: 401 });
      let body: Record<string, unknown>;
      try { body = await request.json() as Record<string, unknown>; } catch { return Response.json({ error: "Invalid request" }, { status: 400 }); }
      const parsed = parseBetaRecipients(body.recipients);
      if (parsed.error || !parsed.recipients) return Response.json({ error: parsed.error }, { status: 400 });
      const platform = body.platform === "iOS" ? "iOS" : "Android";
      const preview = body.preview === true;
      const messages = parsed.recipients.map(recipient => ({ ...recipient, subject: `AudioChoice ${platform} beta instructions`, ...betaWelcomeEmail(recipient.firstName, platform) }));
      if (preview) return Response.json({ ok: true, preview: messages });
      if (!env.BETA_EMAIL_BCC || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(env.BETA_EMAIL_BCC)) return Response.json({ error: "Set BETA_EMAIL_BCC to your email before sending." }, { status: 503 });
      const results = await Promise.all(messages.map(async message => {
        try {
          const sent = await sendEmail(env.RESEND_API_KEY!, { from: "AudioChoice Beta <updates@audiochoiceapp.com>", to: [message.email], bcc: [env.BETA_EMAIL_BCC], reply_to: "support@audiochoiceapp.com", subject: message.subject, text: message.text, html: message.html });
          return { firstName: message.firstName, email: message.email, sent };
        } catch { return { firstName: message.firstName, email: message.email, sent: false }; }
      }));
      return Response.json({ ok: results.every(result => result.sent), results });
    }

    if (url.pathname === "/api/auditor-application" && request.method === "POST") {
      if (!env.RESEND_API_KEY || !env.DB) return Response.json({ error: "Applications are temporarily unavailable" }, { status: 503 });
      let body: Record<string, unknown>;
      try { body = await request.json() as Record<string, unknown>; } catch { return Response.json({ error: "Invalid request" }, { status: 400 }); }
      if (body.website) return Response.json({ ok: true });

      const clean = (value: unknown, maximum: number) => typeof value === "string" ? value.trim().slice(0, maximum) : "";
      const cleanList = (value: unknown) => Array.isArray(value) ? value.filter((item): item is string => typeof item === "string").map(item => item.trim().slice(0, 100)).filter(Boolean).slice(0, 8) : [];
      const email = clean(body.email, 254).toLowerCase();
      const name = clean(body.name, 100), phone = clean(body.phone, 30), location = clean(body.location, 120), country = clean(body.country, 80);
      const availability = clean(body.availability, 40), device = clean(body.device, 60), headphones = clean(body.headphones, 10), taxResidency = clean(body.taxResidency, 40), notes = clean(body.notes, 2000);
      const experience = cleanList(body.experience);
      const accepted = ["ageConfirmed", "sensitiveContentConfirmed", "confidentialityConfirmed", "contractorConfirmed", "contactConsent"].every(key => body[key] === "accepted");
      if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) || !name || !phone || !location || !country || !availability || !device || !headphones || !taxResidency || !accepted) {
        return Response.json({ error: "Complete every required field and acknowledgement." }, { status: 400 });
      }

      const now = new Date().toISOString();
      const hourStart = new Date(Math.floor(Date.now() / 3_600_000) * 3_600_000).toISOString();
      const clientHash = await sha256(`audiochoice-auditor:${request.headers.get("cf-connecting-ip") || "unknown"}`);
      await env.DB.batch([
        env.DB.prepare(`CREATE TABLE IF NOT EXISTS auditor_applications (
          id TEXT PRIMARY KEY, email TEXT NOT NULL UNIQUE, name TEXT NOT NULL, phone TEXT NOT NULL, location TEXT NOT NULL, country TEXT NOT NULL,
          availability TEXT NOT NULL, experience TEXT NOT NULL DEFAULT '[]', device TEXT NOT NULL, headphones TEXT NOT NULL, tax_residency TEXT NOT NULL,
          notes TEXT NOT NULL DEFAULT '', age_confirmed_at TEXT NOT NULL, sensitive_content_confirmed_at TEXT NOT NULL,
          confidentiality_confirmed_at TEXT NOT NULL, contractor_confirmed_at TEXT NOT NULL, contact_consented_at TEXT NOT NULL,
          status TEXT NOT NULL DEFAULT 'submitted', created_at TEXT NOT NULL, updated_at TEXT NOT NULL
        )`),
        env.DB.prepare("CREATE INDEX IF NOT EXISTS auditor_applications_status_idx ON auditor_applications(status)"),
        env.DB.prepare(`CREATE TABLE IF NOT EXISTS auditor_application_submission_limits (
          client_hash TEXT NOT NULL, window_start TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (client_hash, window_start)
        )`),
      ]);
      await env.DB.prepare(`INSERT INTO auditor_application_submission_limits (client_hash, window_start, attempts) VALUES (?, ?, 1)
        ON CONFLICT(client_hash, window_start) DO UPDATE SET attempts = attempts + 1`).bind(clientHash, hourStart).run();
      const rate = await env.DB.prepare("SELECT attempts FROM auditor_application_submission_limits WHERE client_hash = ? AND window_start = ?").bind(clientHash, hourStart).first<{ attempts: number }>();
      if ((rate?.attempts ?? 0) > 3) return Response.json({ error: "Too many applications were submitted. Please try again later." }, { status: 429 });
      ctx.waitUntil(env.DB.prepare("DELETE FROM auditor_application_submission_limits WHERE window_start < ?").bind(new Date(Date.now() - 172_800_000).toISOString()).run());

      await env.DB.prepare(`INSERT INTO auditor_applications (
        id, email, name, phone, location, country, availability, experience, device, headphones, tax_residency, notes,
        age_confirmed_at, sensitive_content_confirmed_at, confidentiality_confirmed_at, contractor_confirmed_at, contact_consented_at, created_at, updated_at
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(email) DO UPDATE SET name=excluded.name, phone=excluded.phone, location=excluded.location, country=excluded.country,
        availability=excluded.availability, experience=excluded.experience, device=excluded.device, headphones=excluded.headphones, tax_residency=excluded.tax_residency,
        notes=excluded.notes, age_confirmed_at=excluded.age_confirmed_at, sensitive_content_confirmed_at=excluded.sensitive_content_confirmed_at,
        confidentiality_confirmed_at=excluded.confidentiality_confirmed_at, contractor_confirmed_at=excluded.contractor_confirmed_at,
        contact_consented_at=excluded.contact_consented_at, updated_at=excluded.updated_at`)
        .bind(crypto.randomUUID(), email, name, phone, location, country, availability, JSON.stringify(experience), device, headphones, taxResidency, notes, now, now, now, now, now, now, now).run();

      const safeName = escapeHtml(name), safeEmail = escapeHtml(email), safePhone = escapeHtml(phone);
      const safeLocation = escapeHtml(`${location}, ${country}`), safeAvailability = escapeHtml(availability);
      const safeExperience = escapeHtml(experience.join(", ") || "Not provided"), safeDevice = escapeHtml(device);
      const safeHeadphones = escapeHtml(headphones), safeTaxResidency = escapeHtml(taxResidency), safeNotes = escapeHtml(notes || "None");
      const [adminNotified, applicantNotified] = await Promise.all([
        sendEmail(env.RESEND_API_KEY, {
          from: "AudioChoice Auditors <updates@audiochoiceapp.com>", to: ["support@audiochoiceapp.com"], reply_to: email,
          subject: `Auditor application — ${name}`,
          text: `New AudioChoice Auditor Program application.\n\nName: ${name}\nEmail: ${email}\nPhone: ${phone}\nLocation: ${location}, ${country}\nAvailability: ${availability}\nExperience: ${experience.join(", ") || "Not provided"}\nDevice: ${device}\nHeadphones: ${headphones}\nTax residency: ${taxResidency}\nNotes: ${notes || "None"}\n\nAll required acknowledgements were recorded at ${now}.`,
          html: emailShell(`<div style="color:#82ec27;font-size:11px;font-weight:800;letter-spacing:1.8px;">NEW APPLICATION</div><h1 style="margin:10px 0 22px;color:#f4f7f4;font-size:30px;line-height:1.18;">${safeName} applied to join the Auditor Program.</h1><table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="border-collapse:separate;border-spacing:0 9px;"><tr><td style="color:#8fa08f;width:145px;">Email</td><td><a href="mailto:${safeEmail}" style="color:#9af04b;text-decoration:none;">${safeEmail}</a></td></tr><tr><td style="color:#8fa08f;">Phone</td><td>${safePhone}</td></tr><tr><td style="color:#8fa08f;">Location</td><td>${safeLocation}</td></tr><tr><td style="color:#8fa08f;">Availability</td><td>${safeAvailability}</td></tr><tr><td style="color:#8fa08f;vertical-align:top;">Experience</td><td>${safeExperience}</td></tr><tr><td style="color:#8fa08f;">Device</td><td>${safeDevice}</td></tr><tr><td style="color:#8fa08f;">Headphones</td><td>${safeHeadphones}</td></tr><tr><td style="color:#8fa08f;">Tax residency</td><td>${safeTaxResidency}</td></tr><tr><td style="color:#8fa08f;vertical-align:top;">Notes</td><td>${safeNotes}</td></tr></table><div style="margin-top:24px;padding:15px 17px;border-radius:12px;background:#142214;color:#c9d8c9;font-size:14px;">All required auditor acknowledgements were recorded with this application.</div>`, `New AudioChoice Auditor Program application from ${name}.`),
        }),
        sendEmail(env.RESEND_API_KEY, {
          from: "AudioChoice Auditors <updates@audiochoiceapp.com>", to: [email], reply_to: "support@audiochoiceapp.com",
          subject: "We received your AudioChoice Auditor application",
          text: `Hi ${name},\n\nThank you for applying to the AudioChoice Auditor Program. We received your application and will review it carefully.\n\nIf we decide to move forward, we’ll email you with next steps, including any agreement and secure tax or payment setup needed before assigning work. Please do not send sensitive financial or tax information by email.\n\nAudioChoice\nListen Your Way.`,
          html: emailShell(`<div style="color:#82ec27;font-size:11px;font-weight:800;letter-spacing:1.8px;">APPLICATION RECEIVED</div><h1 style="margin:10px 0 18px;color:#f4f7f4;font-size:31px;line-height:1.2;">Thank you, ${safeName}.</h1><p style="margin:0 0 20px;">We’ve received your application for the AudioChoice Auditor Program and appreciate your interest in helping listeners enjoy audiobooks on their own terms.</p><div style="margin:24px 0;padding:20px;border:1px solid #37523a;border-radius:14px;background:#102012;"><div style="color:#9af04b;font-size:12px;font-weight:800;letter-spacing:1.4px;">WHAT HAPPENS NEXT</div><p style="margin:9px 0 0;color:#d8e0d8;">Our team will review your application. If we move forward, we’ll email secure next steps for agreements, tax and payment setup, and auditor onboarding before any work is assigned.</p></div><p style="margin:0;">For your protection, please don’t send tax, banking, or other sensitive information by email. Questions? Reply here and our team will help.</p>`, "Your AudioChoice Auditor Program application has been received."),
        }),
      ]);
      if (!adminNotified) console.error("Auditor application admin notification delivery failed");
      if (!applicantNotified) console.error("Auditor application applicant confirmation delivery failed");
      return Response.json({ ok: true });
    }

    if (url.pathname === "/_vinext/image") {
      const allowedWidths = [...DEFAULT_DEVICE_SIZES, ...DEFAULT_IMAGE_SIZES];
      return handleImageOptimization(request, {
        fetchAsset: (path) => env.ASSETS.fetch(new Request(new URL(path, request.url))),
        transformImage: async (body, { width, format, quality }) => {
          const result = await env.IMAGES.input(body).transform(width > 0 ? { width } : {}).output({ format, quality });
          return result.response();
        },
      }, allowedWidths);
    }

    return handler.fetch(request, env, ctx);
  },
};

export default worker;
