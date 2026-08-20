"use client";

import { FormEvent, useEffect, useRef, useState } from "react";

type Account = { email: string; displayName: string; provider: string };
type CompanionStatus = "checking" | "ready" | "missing";

export default function PortalPage() {
  const [account, setAccount] = useState<Account | null>(null);
  const [loading, setLoading] = useState(true);
  const [authError, setAuthError] = useState("");
  const [companion, setCompanion] = useState<CompanionStatus>("checking");
  const [file, setFile] = useState<File | null>(null);
  const [transferStatus, setTransferStatus] = useState("");
  const [progress, setProgress] = useState(0);
  const fileInput = useRef<HTMLInputElement>(null);

  useEffect(() => {
    fetch("/api/portal/account", { credentials: "same-origin" })
      .then(async response => response.ok ? response.json() : null)
      .then(value => setAccount(value?.user ?? null))
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => {
    if (!account) return;
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 1400);
    fetch("http://127.0.0.1:47621/health", { signal: controller.signal })
      .then(response => setCompanion(response.ok ? "ready" : "missing"))
      .catch(() => setCompanion("missing"))
      .finally(() => window.clearTimeout(timeout));
    return () => controller.abort();
  }, [account]);

  async function signIn(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setAuthError("");
    const form = new FormData(event.currentTarget);
    const response = await fetch("/api/portal/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email: form.get("email"), password: form.get("password") }),
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      setAuthError(body.error ?? "AudioChoice could not sign you in.");
      return;
    }
    setAccount(body.user);
  }

  async function signOut() {
    await fetch("/api/portal/logout", { method: "POST" });
    setAccount(null);
    setFile(null);
  }

  async function beginTransfer() {
    if (!file || companion !== "ready") return;
    setTransferStatus("Sending the M4B file to AudioChoice on this computer…");
    setProgress(8);
    const payload = new FormData();
    payload.append("audiobook", file);
    payload.append("relayFallback", "true");
    try {
      const response = await fetch("http://127.0.0.1:47621/v1/transfers", {
        method: "POST",
        body: payload,
      });
      const result = await response.json();
      if (!response.ok) throw new Error(result.error ?? "Conversion could not start.");
      setProgress(100);
      setTransferStatus(result.message ?? "Transfer ready. Scan the QR code with your phone.");
    } catch (error) {
      setProgress(0);
      setTransferStatus(error instanceof Error ? error.message : "The desktop connection was interrupted.");
      setCompanion("missing");
    }
  }

  if (loading) return <main className="portal-page"><div className="portal-loading">Opening your AudioChoice account…</div></main>;

  if (!account) return (
    <main className="portal-page">
      <nav className="portal-nav shell"><a className="brand" href="/"><img src="/audiochoice-logo.png" alt=""/><span>Audio<span>Choice</span></span></a><a href="/">Back to website</a></nav>
      <section className="portal-login shell">
        <div><span className="portal-kicker">AUDIOCHOICE ACCOUNT</span><h1>Your library,<br/><em>connected.</em></h1><p>Sign in with the same AudioChoice email and password you use in the app. Your account information and listening data stay connected; audiobook files remain on your devices.</p></div>
        <form onSubmit={signIn}>
          <img src="/audiochoice-logo.png" alt="AudioChoice"/>
          <h2>Sign in</h2>
          <label>Email address<input name="email" type="email" autoComplete="email" required/></label>
          <label>Password<input name="password" type="password" autoComplete="current-password" required/></label>
          <button className="primary" type="submit">Continue <span>→</span></button>
          {authError && <p className="form-error" role="alert">{authError}</p>}
          <small>Google sign-in for the portal will be added after its web OAuth client is approved. Accounts created with email and password work now.</small>
        </form>
      </section>
    </main>
  );

  return (
    <main className="portal-page">
      <nav className="portal-nav shell"><a className="brand" href="/"><img src="/audiochoice-logo.png" alt=""/><span>Audio<span>Choice</span></span></a><div><span>{account.displayName}</span><button onClick={signOut}>Sign out</button></div></nav>
      <div className="portal-shell shell">
        <aside><p className="active">⇄ <span>Convert & transfer</span></p><p>▤ <span>Account</span></p><p className="soon">▥ <span>Library</span><small>Later</small></p></aside>
        <section className="portal-content">
          <div className="portal-heading"><span className="portal-kicker">DESKTOP TRANSFER</span><h1>Prepare here.<br/><em>Listen anywhere.</em></h1><p>Use the free AudioChoice Companion on your computer to transfer an M4B you have already converted through an authorized method. AudioChoice uses a temporary encrypted relay only when your phone cannot connect directly.</p></div>
          <div className="transfer-steps"><span className="complete">1<b>Choose</b></span><i/><span>2<b>Convert</b></span><i/><span>3<b>Connect</b></span><i/><span>4<b>Import</b></span></div>
          <article className="companion-card">
            <div className={`companion-dot ${companion}`}/><div><small>DESKTOP COMPANION</small><h2>{companion === "ready" ? "AudioChoice is ready" : companion === "checking" ? "Checking this computer…" : "AudioChoice Desktop is required"}</h2><p>{companion === "ready" ? "Your computer is ready. Choose an M4B audiobook below." : "Install and open the free AudioChoice Companion, then come back here to start a phone transfer."}</p></div>
            {companion === "missing" && <a className="portal-companion-link" href="/companion">Get Companion</a>}
          </article>
          <article className={`file-drop ${file ? "selected" : ""}`} onClick={() => fileInput.current?.click()}>
            <input ref={fileInput} type="file" accept=".m4b,audio/mp4" onChange={event => setFile(event.target.files?.[0] ?? null)}/>
            <span>{file ? "✓" : "＋"}</span><h2>{file ? file.name : "Choose your M4B audiobook"}</h2><p>{file ? `${(file.size / 1_000_000).toFixed(0)} MB selected` : "The original remains on your computer. AudioChoice securely sends a temporary M4B copy to your phone."}</p>
          </article>
          <div className="transfer-preferences"><div><b>① Same-network first</b><p>Fastest and no cloud storage. Your phone and computer must be on the same Wi-Fi.</p></div><div><b>② Private relay fallback</b><p>Encrypted temporary upload, resumable download, deletion after verified import, and a 24-hour cleanup backstop.</p></div></div>
          <button className="primary transfer-button" disabled={!file || companion !== "ready"} onClick={beginTransfer}>Connect phone <span>→</span></button>
          {transferStatus && <div className="transfer-progress"><i style={{width: `${progress}%`}}/><p>{transferStatus}</p></div>}
          <p className="transfer-privacy">AudioChoice never adds your audiobook to a shared catalog. The phone verifies the completed M4B before beginning the existing import process.</p>
        </section>
      </div>
    </main>
  );
}
