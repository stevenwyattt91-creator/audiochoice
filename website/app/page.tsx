const features = [
  {
    number: "01",
    title: "Bring your own audiobooks",
    copy: "Import the audiobook files you already own. MP3, M4A, M4B, and AAX support is being built in.",
  },
  {
    number: "02",
    title: "Scan once, listen your way",
    copy: "AudioChoice identifies moments by category, then saves the scan so the same edition does not need to be analyzed again.",
  },
  {
    number: "03",
    title: "You decide what plays",
    copy: "Turn whole categories or individual events on and off for each audiobook. Your choices stay with your account.",
  },
];

const filters = ["Profanity", "Sexual content", "Violence", "Drugs & alcohol", "Self-harm"];

export default function Home() {
  const [updatesOpen, setUpdatesOpen] = useState(false);
  const [email, setEmail] = useState("");
  const [status, setStatus] = useState<"idle" | "sending" | "success" | "error">("idle");

  useEffect(() => {
    if (!updatesOpen) return;
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") setUpdatesOpen(false);
    };
    document.addEventListener("keydown", closeOnEscape);
    return () => document.removeEventListener("keydown", closeOnEscape);
  }, [updatesOpen]);

  const openUpdates = () => {
    setStatus("idle");
    setUpdatesOpen(true);
  };

  const submitUpdates = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setStatus("sending");
    const form = new FormData(event.currentTarget);

    try {
      const response = await fetch("/api/updates", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email, website: form.get("website") }),
      });
      if (!response.ok) throw new Error("Request failed");
      setStatus("success");
    } catch {
      setStatus("error");
    }
  };

  return (
    <main>
      <nav className="nav shell" aria-label="Main navigation">
        <a className="brand" href="#top" aria-label="AudioChoice home">
          <img src="/audiochoice-logo.png" alt="" />
          <span>Audio<span>Choice</span></span>
        </a>
        <div className="nav-links">
          <a href="#how">How it works</a>
          <a href="#parental-controls">Parental controls</a>
          <a href="#privacy">Privacy</a>
          <a href="/companion">Transfer to phone</a>
          <a href="/auditor-application">Auditor Application</a>
          <a href="mailto:support@audiochoiceapp.com">Contact</a>
        </div>
        <div className="nav-actions">
          <a className="beta-nav" href="/android-beta">Join Beta</a>
        </div>
      </nav>

      <section className="hero shell" id="top">
        <div className="hero-copy">
          <div className="eyebrow"><i /> Coming soon to Android and Apple</div>
          <h1>Your audiobooks.<br /><em>Your boundaries.</em></h1>
          <p className="hero-lede">
            AudioChoice is a private audiobook player that finds sensitive content and lets you decide what to hear, mute, or skip.
          </p>
          <div className="hero-actions" id="updates">
            <button className="primary" type="button" onClick={openUpdates}>Keep me updated <span>→</span></button>
            <a className="secondary" href="#how">See how it works</a>
          </div>
          <p className="microcopy">No spam. Just meaningful launch updates.</p>
        </div>

        <div className="hero-visual" aria-label="Preview of the AudioChoice player and filters">
          <div className="glow" />
          <div className="phone">
            <div className="phone-top"><span>9:41</span><b>● ● ▰</b></div>
            <div className="player-title"><span>⌄</span><b>Now Playing</b><span>•••</span></div>
            <div className="cover"><img src="/audiochoice-logo.png" alt="AudioChoice headphone logo" /></div>
            <p className="part">Part 11</p>
            <h2>The Chateau d&apos;If</h2>
            <div className="timeline"><i /></div>
            <div className="times"><span>35:56</span><span>-41:59</span></div>
            <div className="controls"><span>↝</span><span>◀</span><button aria-label="Play preview">▶</button><span>▶</span><span>↜</span></div>
            <div className="player-options"><span><b>1.0x</b>Speed</span><span><b>☷</b>Chapters</span><span><b>◷</b>Sleep</span><span><b>▯</b>Bookmarks</span></div>
          </div>
          <div className="filter-card">
            <div><small>FILTERS ACTIVE</small><strong>Listening your way</strong></div>
            {filters.slice(0, 3).map((filter) => <p key={filter}><span>✓</span>{filter}<i /></p>)}
          </div>
        </div>
      </section>

      <section className="trust-strip">
        <div className="shell trust-inner">
          <p><span>◇</span><b>Private by design</b>Your audio is never added to a public library</p>
          <p><span>◎</span><b>Built around your choices</b>Every book gets its own filter settings</p>
          <p><span>↻</span><b>Scan intelligence that grows</b>Known editions can reuse existing results</p>
        </div>
      </section>

      <section className="how shell" id="how">
        <div className="section-heading">
          <div><span>HOW IT WORKS</span><h2>From file to filtered listening.</h2></div>
          <p>AudioChoice keeps the experience simple while the intelligence works quietly behind the scenes.</p>
        </div>
        <div className="feature-grid">
          {features.map((feature) => (
            <article key={feature.number}>
              <span>{feature.number}</span>
              <h3>{feature.title}</h3>
              <p>{feature.copy}</p>
            </article>
          ))}
        </div>
      </section>

      <section className="control shell">
        <div className="control-copy">
          <span className="label">PRECISE, NOT ALL-OR-NOTHING</span>
          <h2>Filter the moment.<br />Keep the story.</h2>
          <p>See only the content categories found in your audiobook. Open a category to control its subfilters—or review individual events using simple, non-graphic descriptions.</p>
          <ul>
            <li><span>✓</span> Controls unique to every audiobook</li>
            <li><span>✓</span> All detected filters enabled by default</li>
            <li><span>✓</span> Synced choices, bookmarks, and progress</li>
          </ul>
        </div>
        <div className="settings-card">
          <div className="settings-head"><span>‹</span><b>Book Filters</b><small>All on</small></div>
          {filters.map((filter, index) => (
            <div className="filter-row" key={filter}>
              <span className="filter-icon">{["#", "○", "◇", "△", "!"][index]}</span>
              <div><b>{filter}</b><small>{["42 uses", "3 scenes", "18 events", "7 references", "2 references"][index]}</small></div>
              <i className="toggle" />
            </div>
          ))}
        </div>
      </section>

      <section className="parental shell" id="parental-controls">
        <div className="parental-demo" aria-label="Preview of PIN-protected audiobook filters">
          <div className="lock-card">
            <span className="lock-icon">⌁</span>
            <small>PARENTAL CONTROLS</small>
            <strong>Filters locked</strong>
            <p>The selected listening boundaries are protected.</p>
            <div className="pin-dots" aria-hidden="true"><i /><i /><i /><i /></div>
          </div>
          <div className="locked-filter-card">
            <div><span>Sexual content</span><b>On</b></div>
            <div><span>Strong profanity</span><b>On</b></div>
            <div><span>Graphic violence</span><b>On</b></div>
            <p><span>⌕</span> PIN required to change filters</p>
          </div>
        </div>
        <div className="parental-copy">
          <span className="label">PARENT-SET. PIN-PROTECTED.</span>
          <h2>Set the boundaries.<br />Lock them in.</h2>
          <p>Create a private 4–6 digit PIN, choose the filters for an audiobook, and lock those settings before handing over the device. Playback continues to skip enabled content, while filter controls stay protected.</p>
          <ul>
            <li><span>✓</span><div><b>Simple PIN protection</b><small>A short code keeps filter changes with the account owner.</small></div></li>
            <li><span>✓</span><div><b>Book-by-book control</b><small>Choose the right boundaries for each story.</small></div></li>
            <li><span>✓</span><div><b>Filters keep working while locked</b><small>Listeners can play, pause, and navigate without changing protected choices.</small></div></li>
          </ul>
        </div>
      </section>

      <section className="privacy" id="privacy">
        <div className="shell privacy-inner">
          <div className="privacy-mark">⌁</div>
          <div><span>PRIVACY FIRST</span><h2>Your audiobook stays yours.</h2></div>
          <p>Audio files are used only as needed to prepare your private scan and are not retained as an AudioChoice audiobook collection. Your listening data remains connected to your account, so you can pick up where you left off after reimporting the book on another device.</p>
        </div>
      </section>

      <section className="final-cta shell">
        <img src="/audiochoice-logo.png" alt="" />
        <span>COMING SOON</span>
        <h2>Listen Your Way.</h2>
        <p>AudioChoice is being carefully built for listeners who want more control without giving up the books they love.</p>
        <button className="primary" type="button" onClick={openUpdates}>Keep me updated <span>→</span></button>
      </section>

      <footer className="shell">
        <a className="brand" href="#top"><img src="/audiochoice-logo.png" alt="" /><span>Audio<span>Choice</span></span></a>
        <p className="footer-copy">
          © 2026 AudioChoice. Listen Your Way.
          <span>AudioChoice participates in affiliate programs, including Awin. We may earn a commission from qualifying purchases.</span>
        </p>
        <div><a href="#privacy">Privacy</a><a href="mailto:support@audiochoiceapp.com">Support</a></div>
      </footer>

      {updatesOpen && (
        <div className="modal-backdrop" role="presentation" onMouseDown={() => setUpdatesOpen(false)}>
          <section className="updates-modal" role="dialog" aria-modal="true" aria-labelledby="updates-title" onMouseDown={(event) => event.stopPropagation()}>
            <button className="modal-close" type="button" aria-label="Close updates signup" onClick={() => setUpdatesOpen(false)}>×</button>
            {status === "success" ? (
              <div className="signup-success" role="status">
                <span>✓</span>
                <h2>You&apos;re on the list.</h2>
                <p>We&apos;ll email you when there&apos;s meaningful AudioChoice news to share.</p>
                <button className="primary" type="button" onClick={() => setUpdatesOpen(false)}>Done</button>
              </div>
            ) : (
              <>
                <span className="modal-label">LAUNCH UPDATES</span>
                <h2 id="updates-title">Be the first to know.</h2>
                <p>Enter your email and we&apos;ll let you know when AudioChoice is ready for Android and Apple.</p>
                <form onSubmit={submitUpdates}>
                  <label htmlFor="updates-email">Email address</label>
                  <input id="updates-email" name="email" type="email" autoComplete="email" placeholder="you@example.com" value={email} onChange={(event) => setEmail(event.target.value)} required autoFocus />
                  <input className="honeypot" name="website" tabIndex={-1} autoComplete="off" aria-hidden="true" />
                  <button className="primary" type="submit" disabled={status === "sending"}>{status === "sending" ? "Submitting…" : "Keep me updated"}<span>→</span></button>
                  {status === "error" && <p className="form-error" role="alert">We couldn&apos;t submit that right now. Please try again.</p>}
                </form>
                <small>No spam. Unsubscribe anytime.</small>
              </>
            )}
          </section>
        </div>
      )}
    </main>
  );
}
"use client";

import { FormEvent, useEffect, useState } from "react";
