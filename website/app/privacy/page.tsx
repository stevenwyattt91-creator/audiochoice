import type { Metadata } from "next";
import Link from "next/link";

// A server component on purpose: this URL gets submitted to App Store Connect and the Play
// Console, and reviewers plus crawlers need it to render without client-side JavaScript.
export const metadata: Metadata = {
  title: "Privacy Policy — AudioChoice",
  description:
    "How AudioChoice handles your audiobooks, your account, and your listening data. Your audiobook files are never added to a public library or shared with other listeners.",
  alternates: { canonical: "/privacy" },
};

const EFFECTIVE_DATE = "September 3, 2026";

export default function PrivacyPage() {
  return (
    <main className="legal-page">
      <nav className="nav shell" aria-label="Privacy navigation">
        <Link className="brand" href="/" aria-label="AudioChoice home">
          <img src="/audiochoice-logo.png" alt="" />
          <span>Audio<span>Choice</span></span>
        </Link>
        <Link className="beta-back" href="/">← Back to AudioChoice</Link>
      </nav>

      <header className="legal-hero shell">
        <span className="portal-kicker">LEGAL</span>
        <h1>Privacy Policy</h1>
        <p className="legal-effective">Effective {EFFECTIVE_DATE}</p>
      </header>

      <div className="legal-body shell">
        <section className="legal-summary" aria-label="Summary">
          <h2>The short version</h2>
          <ul>
            <li><span>✓</span> Your audiobooks are never added to a public library, shared with other listeners, or redistributed.</li>
            <li><span>✓</span> We do not sell your personal information, and we do not use your listening habits for advertising.</li>
            <li><span>✓</span> Your filter choices, bookmarks, and progress exist to sync your own devices, nothing else.</li>
            <li><span>✓</span> You can ask us to delete your account and everything attached to it.</li>
          </ul>
          <p className="legal-note">
            AudioChoice does need an account and does process your audiobook to find content to filter, so this page
            explains exactly what that involves rather than claiming we collect nothing.
          </p>
        </section>

        <section>
          <h2>Your audiobook files</h2>
          <p>
            AudioChoice does not provide, sell, or distribute audiobooks. You import files you already own from services
            such as Audible, Libro.fm, GraphicAudio, or your own collection.
          </p>
          <p>
            To find the content you want filtered, your audiobook&apos;s audio is processed to produce a transcript and a
            scan of where sensitive moments occur. That processing is what makes filtering possible. Once your scan is
            prepared, we do not retain your audio as an AudioChoice audiobook collection, we do not make it available to
            anyone else, and we do not use it to build a catalog of books for other people to listen to.
          </p>
          <p>
            Where a file is encrypted, such as an Audible AAX file, conversion happens locally on your own device.
            Filtering itself is applied on your device during playback.
          </p>
        </section>

        <section>
          <h2>Your account</h2>
          <p>We create an account so your library and choices can follow you between devices. That account stores:</p>
          <ul className="legal-list">
            <li>Your email address</li>
            <li>Your display name, if you provide one</li>
            <li>A securely hashed password, or an identifier from Apple or Google if you use their sign-in</li>
            <li>Session tokens that keep you signed in</li>
          </ul>
          <p>
            If you sign in with Apple or Google, we receive an account identifier and email address from them. We do not
            receive your password. Their handling of that sign-in is covered by their own privacy policies.
          </p>
        </section>

        <section>
          <h2>Your listening data and filter choices</h2>
          <p>Tied to your account, we store:</p>
          <ul className="legal-list">
            <li>The audiobooks in your library and their identifying details</li>
            <li>Your filter settings, both per book and as reusable profiles, including any custom words you add</li>
            <li>Your playback position, bookmarks, notes, and collections</li>
            <li>Your parental control PIN, stored so filter locks can be enforced</li>
          </ul>
          <p>
            This exists so you can pick up where you left off, including after reimporting a book on another device. It
            is not sold, and it is not used to target advertising at you.
          </p>
        </section>

        <section>
          <h2>Scan results and editions</h2>
          <p>
            When a scan is completed for a specific edition of an audiobook, the resulting map of content locations may
            be reused so that the same edition does not have to be analyzed again, whether for you or for another
            listener who owns that same edition. What is reused is the timing and category information about the work
            itself. Your identity, your library, and your filter choices are not part of that and are not shared.
          </p>
        </section>

        <section>
          <h2>Purchases and subscriptions</h2>
          <p>
            Subscriptions are billed by Apple or Google, not by us. We receive the purchase and entitlement status needed
            to unlock features on your account. We never see your card number. If you took part in an affiliate or
            referral program, we store what is needed to attribute and pay referrals.
          </p>
        </section>

        <section>
          <h2>Support, email, and applications</h2>
          <p>
            If you contact support, apply to a beta, or apply to be an auditor, we keep what you send us so we can
            respond and so we have a record of any consent you gave. Auditor applications include additional details you
            provide, such as your location and tax residency, because that role is paid contractor work.
          </p>
          <p>We send transactional email such as verification, password resets, and replies. We do not sell your email address.</p>
        </section>

        <section>
          <h2>Service providers</h2>
          <p>We use a small number of providers to run AudioChoice, and they process data only to provide their service to us:</p>
          <ul className="legal-list">
            <li>Cloud hosting and databases for the app&apos;s backend and this website</li>
            <li>Speech-to-text and language model providers used to transcribe and classify audiobook content</li>
            <li>A transactional email provider for account and support email</li>
            <li>Apple and Google for sign-in, app distribution, and billing</li>
          </ul>
          <p>
            Some providers operate in the United States, so your information may be processed there. AudioChoice
            participates in affiliate programs, including Awin, and may earn a commission from qualifying purchases made
            through links we share.
          </p>
        </section>

        <section>
          <h2>Children and parental controls</h2>
          <p>
            AudioChoice is designed so an adult can set filters and lock them with a PIN before handing a device to a
            child. The account itself is intended for adults. AudioChoice is not directed to children under 13, and we do
            not knowingly create accounts for them. If you believe a child has created an account, contact us and we will
            remove it.
          </p>
        </section>

        <section>
          <h2>Retention and deleting your data</h2>
          <p>
            We keep your account data while your account exists. You can remove individual books, bookmarks, notes,
            collections, and filter profiles from inside the app at any time.
          </p>
          <p>
            To delete your entire account and the data attached to it, email{" "}
            <a href="mailto:support@audiochoiceapp.com">support@audiochoiceapp.com</a> from your account&apos;s email
            address. We will confirm and then delete it. Some records may be kept where we are required to, such as
            records of consent or of a payment.
          </p>
          <p>
            You may also ask us for a copy of the personal information we hold about you, or ask us to correct it.
            Depending on where you live, you may have additional rights over that information, and you can exercise them
            using the same address.
          </p>
        </section>

        <section>
          <h2>Security</h2>
          <p>
            Passwords are stored hashed, connections are encrypted in transit, and access to production data is limited
            to the people who need it. No service can promise perfect security, but if a breach affects your information
            we will tell you and the relevant authorities where we are required to.
          </p>
        </section>

        <section>
          <h2>Changes to this policy</h2>
          <p>
            If this policy changes in a way that meaningfully affects you, we will update the effective date above and,
            for significant changes, tell you in the app or by email.
          </p>
        </section>

        <section>
          <h2>Contact</h2>
          <p>
            Questions about this policy or about your data:{" "}
            <a href="mailto:support@audiochoiceapp.com">support@audiochoiceapp.com</a>
          </p>
        </section>
      </div>

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
