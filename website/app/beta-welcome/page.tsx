"use client";

import { useState } from "react";
import readXlsxFile from "read-excel-file/browser";

type Preview = { firstName: string; email: string; subject: string; text: string };

export default function BetaWelcomePage() {
  const [token, setToken] = useState("");
  const [rows, setRows] = useState("First Name,Email\n");
  const [platform, setPlatform] = useState<"iOS" | "Android">("iOS");
  const [preview, setPreview] = useState<Preview[]>([]);
  const [status, setStatus] = useState("");
  const [busy, setBusy] = useState(false);

  async function importSpreadsheet(file: File) {
    setStatus("");
    setPreview([]);
    try {
      let sheetRows: unknown[][];
      if (file.name.toLowerCase().endsWith(".csv")) {
        const text = await file.text();
        sheetRows = text.split(/\r?\n/).filter(Boolean).map(line => line.split(",").map(cell => cell.trim().replace(/^"|"$/g, "")));
      } else {
        sheetRows = await readXlsxFile(file);
      }

      const normalized = sheetRows.map(row => row.map(cell => String(cell ?? "").trim()));
      const header = normalized[0]?.map(cell => cell.toLowerCase().replace(/[^a-z]/g, "")) ?? [];
      const firstNameIndex = header.findIndex(cell => cell === "firstname" || cell === "name");
      const emailIndex = header.findIndex(cell => cell === "email" || cell === "emailaddress");
      if (firstNameIndex < 0 || emailIndex < 0) throw new Error("The spreadsheet needs First Name and Email columns in the first row.");

      const recipients = normalized.slice(1)
        .map(row => [row[firstNameIndex], row[emailIndex]])
        .filter(([firstName, email]) => firstName && email);
      if (!recipients.length) throw new Error("No beta users were found in the spreadsheet.");
      if (recipients.length > 100) throw new Error("Upload at most 100 beta users at a time.");
      setRows(["First Name,Email", ...recipients.map(([firstName, email]) => `${firstName},${email}`)].join("\n"));
      setStatus(`Loaded ${recipients.length} beta user(s) from ${file.name}. Review them, then preview the messages.`);
    } catch (error) {
      setStatus(error instanceof Error ? error.message : "The spreadsheet could not be read.");
    }
  }

  async function call(send: boolean) {
    setBusy(true); setStatus("");
    try {
      const response = await fetch("/api/beta-welcome", { method: "POST", headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` }, body: JSON.stringify({ recipients: rows, platform, preview: !send }) });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(body.error ?? "The email tool could not complete that request.");
      if (!send) { setPreview(body.preview ?? []); setStatus(`${(body.preview ?? []).length} personalized message(s) ready to review.`); }
      else { const failed = (body.results ?? []).filter((item: { sent: boolean }) => !item.sent).length; setStatus(failed ? `Sent with ${failed} delivery failure(s). Review the results below.` : `Sent ${body.results.length} email(s). Your BCC copy should arrive shortly.`); }
    } catch (error) { setStatus(error instanceof Error ? error.message : "The email tool could not complete that request."); }
    finally { setBusy(false); }
  }

  return <main style={{ minHeight: "100vh", background: "#071008", color: "#edf2ed", padding: "48px 20px", fontFamily: "Arial, sans-serif" }}><section style={{ maxWidth: 820, margin: "0 auto", background: "#0c160e", border: "1px solid #304133", borderRadius: 22, padding: 32 }}><p style={{ color: "#82ec27", letterSpacing: 2, fontWeight: 800, fontSize: 12 }}>AUDIOCHOICE INTERNAL TOOL</p><h1 style={{ fontSize: 38, margin: "10px 0" }}>Beta welcome emails</h1><p style={{ color: "#afbdaf", lineHeight: 1.6 }}>Upload an Excel or CSV list, or paste testers below. Review every personalized message before sending. This page is protected by your private admin token.</p><label style={{ display: "block", marginTop: 24 }}>Private admin token<input value={token} onChange={e => setToken(e.target.value)} type="password" style={inputStyle} /></label><div style={{ display: "flex", gap: 20, marginTop: 22 }}><label>Platform<select value={platform} onChange={e => setPlatform(e.target.value as "iOS" | "Android")} style={inputStyle}><option>iOS</option><option>Android</option></select></label></div><label style={{ display: "block", marginTop: 22 }}>Upload Excel or CSV<span style={{ display: "block", marginTop: 6, color: "#8fa08f", fontSize: 13 }}>The first row must contain <strong>First Name</strong> and <strong>Email</strong>. Up to 100 users per send.</span><input type="file" accept=".xlsx,.xls,.csv" onChange={e => { const file = e.target.files?.[0]; if (file) importSpreadsheet(file); e.target.value = ""; }} style={{ ...inputStyle, padding: 10 }} /></label><label style={{ display: "block", marginTop: 22 }}>First Name,Email<textarea value={rows} onChange={e => { setRows(e.target.value); setPreview([]); }} rows={9} placeholder={"First Name,Email\nJordan,jordan@example.com"} style={{ ...inputStyle, fontFamily: "monospace", resize: "vertical" }} /></label><div style={{ display: "flex", gap: 12, marginTop: 18, flexWrap: "wrap" }}><button disabled={busy || !token} onClick={() => call(false)} style={buttonStyle}>{busy ? "Working…" : "Preview messages"}</button><button disabled={busy || !token || !preview.length} onClick={() => { if (window.confirm(`Send ${preview.length} personalized beta email(s) now?`)) call(true); }} style={{ ...buttonStyle, background: "#263529", color: "#edf2ed" }}>Send emails</button></div>{status && <p role="status" style={{ marginTop: 20, color: status.includes("failure") || status.includes("not") || status.includes("could not") || status.includes("needs") ? "#ffb4a8" : "#9af04b" }}>{status}</p>}{preview.length > 0 && <div style={{ marginTop: 28, borderTop: "1px solid #304133", paddingTop: 22 }}><h2>Preview</h2>{preview.map(message => <article key={message.email} style={{ border: "1px solid #304133", borderRadius: 12, padding: 16, marginTop: 12 }}><strong>{message.firstName} · {message.email}</strong><p style={{ whiteSpace: "pre-wrap", color: "#c9d8c9", lineHeight: 1.5 }}>{message.text}</p></article>)}</div>}</section></main>;
}

const inputStyle = { display: "block", width: "100%", boxSizing: "border-box" as const, marginTop: 8, padding: "13px 14px", borderRadius: 10, border: "1px solid #405342", background: "#071008", color: "#edf2ed", fontSize: 16 };
const buttonStyle = { border: 0, borderRadius: 10, padding: "13px 18px", background: "#82ec27", color: "#071008", fontWeight: 800, fontSize: 15, cursor: "pointer" };
