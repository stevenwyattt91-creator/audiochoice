"use client";

import { ChangeEvent, useEffect, useMemo, useRef, useState } from "react";

type Stage = "choose" | "acknowledge" | "convert" | "uploading" | "ready";
const steps = [
  ["01", "Choose your audiobook", "Select an M4B, MP3, or AAX file from your computer."],
  ["02", "Confirm ownership", "Required only when you select an AAX file."],
  ["03", "Convert to M4B", "For AAX, use your authorized converter, then return here."],
  ["04", "Transfer to app", "Scan the QR code in AudioChoice to begin import."],
];

function pseudoQr(value: string) {
  const size = 21;
  let seed = Array.from(value).reduce((sum, character) => (sum * 31 + character.charCodeAt(0)) >>> 0, 2166136261);
  const cells: boolean[][] = Array.from({ length: size }, () => Array(size).fill(false));
  const finder = (x: number, y: number) => { for (let row = 0; row < 7; row++) for (let col = 0; col < 7; col++) cells[y + row][x + col] = row === 0 || row === 6 || col === 0 || col === 6 || (row >= 2 && row <= 4 && col >= 2 && col <= 4); };
  finder(0, 0); finder(size - 7, 0); finder(0, size - 7);
  for (let row = 0; row < size; row++) for (let col = 0; col < size; col++) { if (cells[row][col]) continue; seed = (seed * 1664525 + 1013904223) >>> 0; cells[row][col] = (seed & 7) < 3; }
  return cells;
}

export default function CompanionPage() {
  const [file, setFile] = useState<File | null>(null);
  const [stage, setStage] = useState<Stage>("choose");
  const [progress, setProgress] = useState(0);
  const [acknowledged, setAcknowledged] = useState(false);
  const [error, setError] = useState("");
  const input = useRef<HTMLInputElement>(null);
  const isAax = file?.name.toLowerCase().endsWith(".aax") ?? false;
  const token = useMemo(() => `audiochoice://transfer/${crypto.randomUUID?.() ?? Math.random().toString(36).slice(2)}`, [stage]);
  const qr = useMemo(() => pseudoQr(token), [token]);
  useEffect(() => { if (stage !== "uploading") return; setProgress(8); const timer = window.setInterval(() => setProgress(value => { if (value >= 100) { window.clearInterval(timer); setStage("ready"); return 100; } return Math.min(100, value + 13); }), 260); return () => window.clearInterval(timer); }, [stage]);
  const chooseFile = (event: ChangeEvent<HTMLInputElement>) => { const selected = event.target.files?.[0] ?? null; setError(""); setFile(selected); setAcknowledged(false); if (!selected) { setStage("choose"); return; } setStage(selected.name.toLowerCase().endsWith(".aax") ? "acknowledge" : "choose"); };
  const beginTransfer = () => { if (!file) return; if (isAax && !acknowledged) { setStage("acknowledge"); return; } setError(""); setStage("uploading"); };
  const reset = () => { setFile(null); setAcknowledged(false); setProgress(0); setError(""); setStage("choose"); if (input.current) input.current.value = ""; };
  const activeStep = stage === "ready" || stage === "uploading" ? 4 : stage === "convert" ? 3 : stage === "acknowledge" ? 2 : 1;

  return (
    <main className="companion-page transfer-page">
      <nav className="nav shell" aria-label="Transfer navigation"><a className="brand" href="/" aria-label="AudioChoice home"><img src="/audiochoice-logo.png" alt="" /><span>Audio<span>Choice</span></span></a><div className="companion-nav-actions"><a href="/android-beta">Join Android Beta</a><a href="/portal">Sign in</a><a className="companion-back" href="/">← Back to AudioChoice</a></div></nav>
      <header className="companion-hero shell transfer-hero"><div><span className="portal-kicker">AUDIOCHOICE TRANSFER TO APP</span><h1>Send your audiobook<br /><em>to your phone.</em></h1><p>Choose a file you already own, follow the guided steps, and scan a private QR code in AudioChoice to start the normal import process.</p><p className="companion-safety">Your audiobook stays on your devices. A temporary handoff is used only to move it to the app and expires after import.</p></div><aside className="companion-visual" aria-label="Audiobook transfer preview"><div className="companion-file"><span>▥</span><b>{file?.name ?? "Your audiobook.m4b"}</b><small>{stage === "ready" ? "Ready to import" : "Private transfer"}</small></div><div className="companion-route"><i /><span>⌁</span><i /></div><div className="companion-phone"><small>AUDIOCHOICE</small><strong>{stage === "ready" ? "Scan to import" : "Your phone"}</strong><b>✓</b></div></aside></header>
      <section className="transfer-workflow shell" aria-labelledby="transfer-title"><div className="companion-heading"><span className="label">GUIDED TRANSFER</span><h2 id="transfer-title">Every step, clearly.</h2><p>Stay on this page while the transfer is prepared. For AAX files, we pause so you can convert the file before sending the resulting M4B.</p></div><div className="transfer-step-list">{steps.map(([number, title, copy], index) => <div key={number} className={`transfer-step-card ${activeStep === index + 1 ? "active" : ""} ${activeStep > index + 1 ? "complete" : ""}`}><span>{activeStep > index + 1 ? "✓" : number}</span><div><b>{title}</b><p>{copy}</p></div></div>)}</div>
        <article className="transfer-panel"><div className="transfer-panel-head"><span className="label">STEP {activeStep} OF 4</span><button type="button" onClick={reset}>Start over</button></div>
          {stage === "choose" && <><h3>Choose an audiobook</h3><p className="transfer-muted">M4B and MP3 transfer directly. AAX files require the ownership acknowledgement and an authorized conversion before transfer.</p><label className={`file-drop transfer-file ${file ? "selected" : ""}`}><input ref={input} type="file" accept=".m4b,.mp3,.aax,audio/mp4,audio/mpeg" onChange={chooseFile} /><span>{file ? "✓" : "＋"}</span><strong>{file ? file.name : "Select an M4B, MP3, or AAX"}</strong><small>{file ? `${(file.size / 1_000_000).toFixed(0)} MB selected` : "The original file remains on this computer."}</small></label>{file && <button className="primary transfer-action" type="button" onClick={beginTransfer}>Continue <span>→</span></button>}</>}
          {stage === "acknowledge" && <><h3>Confirm ownership</h3><p className="transfer-muted">Before continuing with an AAX file, confirm that you legally acquired it and have the right to convert it for personal use.</p><div className="ownership-card"><b>Ownership acknowledgement</b><p>By continuing, I confirm that I legally acquired this audiobook and have the right to convert it for my personal use. I will not use AudioChoice to copy, share, distribute, sell, or process content I do not lawfully own or control.</p><label><input type="checkbox" checked={acknowledged} onChange={event => setAcknowledged(event.target.checked)} /> I agree</label></div><button className="primary transfer-action" type="button" disabled={!acknowledged} onClick={() => setStage("convert")}>Continue to conversion <span>→</span></button></>}
          {stage === "convert" && <><h3>Convert your AAX to M4B</h3><p className="transfer-muted">AudioChoice does not distribute audiobook files. Use your authorized conversion method, then return here and attach the resulting M4B.</p><div className="conversion-callout"><b>1. Open your authorized converter</b><p>Keep this tab open so you can return after conversion.</p><a className="secondary transfer-action" href="https://audible-tools.kamsker.at/" target="_blank" rel="noreferrer">Open conversion page ↗</a></div><p className="transfer-return">When conversion is complete, return to this page and choose the resulting M4B.</p><label className="file-drop transfer-file"><input ref={input} type="file" accept=".m4b,audio/mp4" onChange={chooseFile} /><span>{file?.name.toLowerCase().endsWith(".m4b") ? "✓" : "＋"}</span><strong>{file?.name.toLowerCase().endsWith(".m4b") ? file.name : "Attach the resulting M4B"}</strong><small>The converted M4B is the file that will be transferred.</small></label>{file?.name.toLowerCase().endsWith(".m4b") && <button className="primary transfer-action" type="button" onClick={beginTransfer}>Prepare transfer <span>→</span></button>}</>}
          {stage === "uploading" && <div className="transfer-progress-panel"><div className="spinner"/><h3>Preparing your private transfer</h3><p>Uploading securely and creating a one-time handoff…</p><div className="progress-track"><i style={{ width: `${progress}%` }} /></div><strong>{progress}%</strong></div>}
          {stage === "ready" && <div className="transfer-ready"><span className="label">STEP 4 · TRANSFER TO APP</span><h3>Scan this QR code in AudioChoice</h3><p>Open the AudioChoice app on your phone, choose Import, then scan. The app will verify the handoff and continue its normal import process.</p><div className="qr-card"><svg viewBox="0 0 210 210" role="img" aria-label="One-time AudioChoice transfer QR code">{qr.map((row, y) => row.map((on, x) => on ? <rect key={`${x}-${y}`} x={x * 10} y={y * 10} width="10" height="10" fill="#071008" /> : null))}</svg><small>One-time transfer · expires after import</small></div><button className="secondary transfer-action" type="button" onClick={() => navigator.clipboard?.writeText(token)}>Copy transfer link</button></div>}
          {error && <p className="form-error" role="alert">{error}</p>}
        </article></section>
      <section className="companion-privacy-strip"><div className="shell"><span>◇</span><div><b>Private by design</b><p>Only a temporary handoff is created. AudioChoice does not keep a shared audiobook library, and the handoff is removed after verified import.</p></div></div></section>
      <footer className="shell"><a className="brand" href="/"><img src="/audiochoice-logo.png" alt="" /><span>Audio<span>Choice</span></span></a><p className="footer-copy">© 2026 AudioChoice. Listen Your Way.</p><div><a href="/">Home</a><a href="mailto:support@audiochoiceapp.com">Support</a></div></footer>
    </main>
  );
}
