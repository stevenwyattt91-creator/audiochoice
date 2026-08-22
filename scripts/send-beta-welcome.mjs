#!/usr/bin/env node

import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const [, , firstName, testerEmail] = process.argv;
const apiKey = process.env.RESEND_API_KEY;
const previewOnly = process.argv.includes("--preview");

if (!firstName || !testerEmail) {
  console.error('Usage: node scripts/send-beta-welcome.mjs "First name" "tester@example.com"');
  process.exit(1);
}

if (!apiKey && !previewOnly) {
  console.error("RESEND_API_KEY is not set.");
  process.exit(1);
}

const escapeHtml = (value) => value
  .replaceAll("&", "&amp;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;")
  .replaceAll('"', "&quot;")
  .replaceAll("'", "&#39;");

const scriptDir = dirname(fileURLToPath(import.meta.url));
const templateDir = resolve(scriptDir, "../docs/email-templates");
const replacements = {
  "{{FIRST_NAME}}": escapeHtml(firstName),
  "{{TESTER_EMAIL}}": escapeHtml(testerEmail),
};

const personalize = (template) => Object.entries(replacements)
  .reduce((result, [placeholder, value]) => result.replaceAll(placeholder, value), template);

const [htmlTemplate, textTemplate] = await Promise.all([
  readFile(resolve(templateDir, "android-beta-welcome.html"), "utf8"),
  readFile(resolve(templateDir, "android-beta-welcome.txt"), "utf8"),
]);

const personalizedHtml = personalize(htmlTemplate);
const personalizedText = personalize(textTemplate.replace(/^Subject:.*\n+/i, ""));

if (previewOnly) {
  const safePreviewName = firstName.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "") || "tester";
  const previewPath = resolve(templateDir, `preview-${safePreviewName}.html`);
  await writeFile(previewPath, personalizedHtml, "utf8");
  console.log(previewPath);
  process.exit(0);
}

const response = await fetch("https://api.resend.com/emails", {
  method: "POST",
  headers: {
    Authorization: `Bearer ${apiKey}`,
    "Content-Type": "application/json",
  },
  body: JSON.stringify({
    from: "AudioChoice Beta <support@audiochoiceapp.com>",
    to: [testerEmail],
    bcc: ["steven.wyatt@audiochoiceapp.com"],
    reply_to: "support@audiochoiceapp.com",
    subject: "Welcome to the AudioChoice Android Beta",
    html: personalizedHtml,
    text: personalizedText,
  }),
});

const result = await response.json().catch(() => ({}));
if (!response.ok) {
  console.error(`Resend rejected the email (${response.status}): ${result.message ?? JSON.stringify(result)}`);
  process.exit(1);
}

console.log(`Beta welcome sent to ${testerEmail}. Resend ID: ${result.id}`);
