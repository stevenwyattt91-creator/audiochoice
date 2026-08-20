"use client";

import { FormEvent, useState } from "react";

const filterTypes = ["Sexual content", "Profanity", "Violence", "Blasphemy", "Drugs & alcohol", "Custom words or phrases"];
const testItems = [
  "Whether filters trigger at the correct times",
  "Whether clean content is skipped by mistake",
  "Whether any scenes that should be filtered are missed",
  "Whether playback resumes correctly after a filtered section",
  "Any bugs or issues you encounter while listening",
];

export default function AndroidBetaPage() {
  const [status, setStatus] = useState<"idle" | "sending" | "success" | "error">("idle");
  const [error, setError] = useState("");

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const formElement = event.currentTarget;
    setStatus("sending");
    setError("");
    const form = new FormData(formElement);
    const payload = Object.fromEntries(form.entries());
    try {
      const response = await fetch("/api/android-beta", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      const responseText = await response.text();
      let result: { error?: string | null } = {};
      if (responseText) {
        try {
          result = JSON.parse(responseText) as { error?: string | null };
        } catch {
          // A successful submission does not depend on an optional JSON body.
        }
      }
      if (!response.ok) throw new Error(result.error || "Application could not be submitted");
      setStatus("success");
      formElement.reset();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Please try again.");
      setStatus("error");
    }
  };

  return (
    <main className="beta-page">
      <nav className="nav shell" aria-label="Beta navigation">
        <a className="brand" href="/" aria-label="AudioChoice home">
          <img src="/audiochoice-logo.png" alt="" />
          <span>Audio<span>Choice</span></span>
        </a>
        <a className="beta-back" href="/">← Back to AudioChoice</a>
      </nav>

      <header className="beta-hero shell">
        <div>
          <span className="beta-kicker">LIMITED ANDROID BETA</span>
          <h1>ACOTAR GraphicAudio<br /><em>fans wanted.</em></h1>
          <p>Help AudioChoice make audiobook filtering more accurate, reliable, and natural before launch.</p>
          <a className="primary" href="#join-beta">Join the beta <span>↓</span></a>
        </div>
        <aside className="beta-book-card">
          <span>TEST EDITION</span>
          <img src="/audiochoice-logo.png" alt="AudioChoice" />
          <strong>A Court of Thorns and Roses</strong>
          <p>Dramatized Adaptation · GraphicAudio</p>
          <small>Legitimate ownership required</small>
        </aside>
      </header>

      <section className="beta-intro shell">
        <div>
          <span className="label">WHAT IS AUDIOCHOICE?</span>
          <h2>Enjoy the story.<br />Choose your boundaries.</h2>
        </div>
        <div>
          <p>AudioChoice is an audiobook app that lets you customize your listening experience by automatically skipping content based on your personal preferences.</p>
          <p>The goal is simple: let people enjoy their audiobooks the way they want to listen.</p>
        </div>
      </section>

      <section className="beta-filters shell" aria-label="Available filter categories">
        {filterTypes.map((item, index) => <div key={item}><span>0{index + 1}</span><b>{item}</b><i>✓</i></div>)}
      </section>

      <section className="beta-test shell">
        <div className="beta-test-copy">
          <span className="label">WHAT YOU&apos;LL TEST</span>
          <h2>Listen normally.<br />Tell us what happens.</h2>
          <p>This beta focuses exclusively on <strong>A Court of Thorns and Roses: Dramatized Adaptation</strong> from GraphicAudio. Your feedback will improve filter accuracy before launch.</p>
        </div>
        <div className="beta-checklist">
          {testItems.map((item) => <p key={item}><span>✓</span>{item}</p>)}
        </div>
      </section>

      <section className="beta-details shell">
        <article>
          <span>REQUIREMENTS</span>
          <h3>Who can participate</h3>
          <ul>
            <li>An Android phone</li>
            <li>Own the specified GraphicAudio audiobook</li>
            <li>Willing to provide feedback while listening</li>
            <li>Able to report incorrect or missing filter timestamps</li>
          </ul>
        </article>
        <article className="benefit-card">
          <span>BETA BENEFITS</span>
          <h3>A thank-you for helping us build.</h3>
          <p>Active testers receive free app access throughout the beta.</p>
          <p>After launch, Founding Beta Testers receive a lifetime discounted subscription rate of <strong>$2.99/month</strong> instead of the regular <strong>$5.99/month</strong>.</p>
        </article>
      </section>

      <section className="beta-form-section" id="join-beta">
        <div className="shell beta-form-grid">
          <div>
            <span className="label">JOIN THE ANDROID BETA</span>
            <h2>Help make every skip feel seamless.</h2>
            <p>Complete the application below. We&apos;ll review it and contact selected testers using the email provided.</p>
            <div className="ownership-note"><span>◇</span><p><b>Bring your own audiobook.</b><br />AudioChoice does not provide or distribute audiobook files. You must own a legitimate copy.</p></div>
          </div>
          <div className="beta-form-card">
            {status === "success" ? (
              <div className="beta-success" role="status"><span>✓</span><h3>Application received.</h3><p>You&apos;ve been added to the Android beta applicant group. We&apos;ll be in touch if you&apos;re selected.</p></div>
            ) : (
              <form onSubmit={submit}>
                <label className="beta-field" htmlFor="beta-name">
                  <span>Name</span>
                  <input id="beta-name" name="name" type="text" autoComplete="name" required maxLength={100} />
                </label>
                <label className="beta-field" htmlFor="beta-email">
                  <span>Email address</span>
                  <input id="beta-email" name="email" type="email" autoComplete="email" required maxLength={254} />
                </label>
                <label className="beta-field" htmlFor="ownership">
                  <span>Where do you own the audiobook?</span>
                  <select id="ownership" name="ownershipSource" required defaultValue="">
                    <option value="" disabled>Select one</option>
                    <option>Audible</option><option>GraphicAudio</option><option>Libro.fm</option><option>Apple Books</option><option>Google Play Books</option><option>Other</option>
                  </select>
                </label>
                <label className="beta-field" htmlFor="monthly">
                  <span>About how many audiobooks do you listen to each month?</span>
                  <select id="monthly" name="audiobooksPerMonth" required defaultValue="">
                    <option value="" disabled>Select one</option>
                    <option>Less than 1</option><option>1–2</option><option>3–5</option><option>6–10</option><option>More than 10</option>
                  </select>
                </label>
                <input className="honeypot" name="website" tabIndex={-1} autoComplete="off" aria-hidden="true" />
                <label className="beta-consent">
                  <input name="consent" type="checkbox" value="accepted" required />
                  <span>I agree to receive Android beta application and testing emails from AudioChoice.</span>
                </label>
                <button className="primary" type="submit" disabled={status === "sending"}>{status === "sending" ? "Submitting…" : "Join Beta"}<span>→</span></button>
                {status === "error" && <p className="form-error" role="alert">{error}</p>}
                <small>We use your information only to manage the AudioChoice beta.</small>
              </form>
            )}
          </div>
        </div>
      </section>

      <footer className="shell beta-footer">
        <p className="footer-copy">
          © 2026 AudioChoice. Listen Your Way.
          <span>AudioChoice participates in affiliate programs, including Awin. We may earn a commission from qualifying purchases.</span>
        </p>
        <a href="mailto:support@audiochoiceapp.com">Questions? Contact support</a>
      </footer>
    </main>
  );
}
