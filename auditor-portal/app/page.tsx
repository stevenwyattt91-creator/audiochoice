"use client";

import { FormEvent, useEffect, useRef, useState } from "react";

const API =
  process.env.NEXT_PUBLIC_AUDIOCHOICE_API_URL ||
  "https://audiochoice-stg-api.grayocean-b35d4bf9.eastus.azurecontainerapps.io";
type Access = { userID: string; displayName: string; role: string };
type Audit = {
  id: string;
  title: string;
  author?: string;
  edition?: string;
  candidateCount: number;
  reviewedCount: number;
  status: string;
  compensationAmount?: number;
  paymentStatus: string;
  reviewFocus: string;
};
type Candidate = {
  id: string;
  startSeconds: number;
  endSeconds: number;
  categoryID: string;
  confidence: number;
  safeDescription: string;
  stableKey: string;
  listenFromSeconds: number;
  listenToSeconds: number;
};
type Decision = {
  candidateID: string;
  decision: string;
  correctedCategoryID?: string;
  correctedStartSeconds?: number;
  correctedEndSeconds?: number;
  notes?: string;
};
type WorkspaceData = {
  assignment: Audit;
  candidates: Candidate[];
  categories: { id: string; name: string }[];
  decisions: Decision[];
  reviewMediaAvailable: boolean;
};
type Earnings = {
  thisWeek: number;
  awaitingApproval: number;
  approvedUnpaid: number;
  paidThisWeek: number;
};

const money = (amount?: number) =>
  new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" }).format(
    amount || 0,
  );
const timestamp = (seconds: number) =>
  `${Math.floor(seconds / 3600) ? `${Math.floor(seconds / 3600)}:` : ""}${String(Math.floor((seconds % 3600) / 60)).padStart(Math.floor(seconds / 3600) ? 2 : 1, "0")}:${String(Math.floor(seconds % 60)).padStart(2, "0")}`;

export default function Home() {
  const [token, setToken] = useState("");
  const [user, setUser] = useState<Access | null>(null);
  const [audits, setAudits] = useState<Audit[]>([]);
  const [earnings, setEarnings] = useState<Earnings | null>(null);
  const [active, setActive] = useState<WorkspaceData | null>(null);
  const [index, setIndex] = useState(0);
  const [error, setError] = useState("");
  const call = async (
    path: string,
    init: RequestInit = {},
    supplied = token,
  ) => {
    const r = await fetch(API + path, {
      ...init,
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${supplied}`,
        ...init.headers,
      },
    });
    if (!r.ok)
      throw new Error(
        r.status === 401
          ? "Your session expired. Please sign in again."
          : "AudioChoice could not complete that request.",
      );
    return r.status === 204 ? null : r.json();
  };
  const load = async (supplied = token) => {
    const [me, tasks, totals] = await Promise.all([
      call("/v1/internal/me", {}, supplied),
      call("/v1/internal/audits", {}, supplied),
      call("/v1/internal/earnings", {}, supplied),
    ]);
    setUser(me);
    setAudits(tasks);
    setEarnings(totals);
  };
  useEffect(() => {
    const saved = sessionStorage.getItem("audit-token");
    if (saved) {
      setToken(saved);
      load(saved).catch((e) => setError(e.message));
    }
  }, []);
  const login = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    setError("");
    try {
      const r = await fetch(API + "/v1/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          email: form.get("email"),
          password: form.get("password"),
        }),
      });
      if (!r.ok) throw new Error("Email or password is incorrect.");
      const value = await r.json();
      sessionStorage.setItem("audit-token", value.accessToken);
      setToken(value.accessToken);
      await load(value.accessToken);
    } catch (e) {
      setError((e as Error).message);
    }
  };
  const open = async (audit: Audit) => {
    try {
      if (audit.status === "available")
        await call(`/v1/internal/audits/${audit.id}/claim`, { method: "POST" });
      const workspace = (await call(
        `/v1/internal/audits/${audit.id}`,
      )) as WorkspaceData;
      setActive(workspace);
      setIndex(
        Math.min(
          workspace.decisions.length,
          Math.max(0, workspace.candidates.length - 1),
        ),
      );
      await load();
    } catch (e) {
      setError((e as Error).message);
    }
  };
  if (!user)
    return (
      <main className="login">
        <section>
          <div className="brand">
            Audio<b>Choice</b>
          </div>
          <p className="eyebrow">INTERNAL AUDITOR PORTAL</p>
          <h1>Secure sign in</h1>
          <p>Approved auditors and administrators only.</p>
          <form onSubmit={login}>
            <label>
              Email
              <input name="email" type="email" required />
            </label>
            <label>
              Password
              <input name="password" type="password" required />
            </label>
            {error && <p className="error">{error}</p>}
            <button>Sign in</button>
          </form>
        </section>
      </main>
    );
  if (active)
    return (
      <Workspace
        data={active}
        index={index}
        token={token}
        back={() => {
          setActive(null);
          load().catch((e) => setError(e.message));
        }}
        previous={() => setIndex((i) => Math.max(0, i - 1))}
        next={() =>
          setIndex((i) => Math.min(active.candidates.length - 1, i + 1))
        }
        refresh={(decision: Decision) =>
          setActive((current) =>
            current
              ? {
                  ...current,
                  decisions: [
                    ...current.decisions.filter(
                      (d) => d.candidateID !== decision.candidateID,
                    ),
                    decision,
                  ],
                }
              : current,
          )
        }
      />
    );
  // An assignment is claimed atomically by the API. Once claimed, it is only
  // returned to that auditor (or an administrator), so it no longer appears in
  // the shared available queue.
  const availableAudits = audits.filter((a) => a.status === "available");
  const currentAudits = audits.filter((a) => a.status === "in_progress");
  const completedAudits = audits.filter(
    (a) => a.status !== "available" && a.status !== "in_progress",
  );
  const AuditCard = ({
    audit,
    completed = false,
  }: {
    audit: Audit;
    completed?: boolean;
  }) => (
    <article className="card" key={audit.id}>
      <div>
        <span className="pill">{audit.status.replaceAll("_", " ")}</span>
        <h2>{audit.title}</h2>
        <p>
          {audit.author || "Unknown author"} ·{" "}
          {audit.edition || "Edition not specified"}
        </p>
        <small>{audit.reviewFocus}</small>
        <progress value={audit.reviewedCount} max={audit.candidateCount} />
        <small>
          {audit.reviewedCount} of {audit.candidateCount} events reviewed · Task
          value: <b>{money(audit.compensationAmount)}</b> ·{" "}
          {audit.paymentStatus.replaceAll("_", " ")}
        </small>
      </div>
      {!completed && (
        <button onClick={() => open(audit)}>
          {audit.status === "in_progress" ? "Continue audit" : "Start audit"}
        </button>
      )}
    </article>
  );
  return (
    <main>
      <header>
        <div className="brand">
          Audio<b>Choice</b>
        </div>
        <div>
          {user.displayName} · {user.role}
          <button
            className="ghost"
            onClick={() => {
              sessionStorage.clear();
              location.reload();
            }}
          >
            Sign out
          </button>
        </div>
      </header>
      <section className="shell">
        <p className="eyebrow">AUDITOR DASHBOARD</p>
        <h1>Focused review queue</h1>
        <p className="muted">
          Each job is organized by audiobook. Verify the listed listening range
          against your authorized copy, check off every event, then submit the
          completed job for admin review and payout.
        </p>
        {error && <p className="error">{error}</p>}
        <div className="stats">
          <article>
            <strong>{availableAudits.length}</strong>Available
          </article>
          <article>
            <strong>{currentAudits.length}</strong>Current jobs
          </article>
          <article>
            <strong>{money(earnings?.approvedUnpaid)}</strong>Approved, unpaid
          </article>
          <article>
            <strong>{money(earnings?.awaitingApproval)}</strong>Awaiting admin
            review
          </article>
        </div>
        <section className="queue">
          <div className="section-heading">
            <div>
              <p className="eyebrow">AVAILABLE AUDIT JOBS</p>
              <h2>Ready to review</h2>
            </div>
            <span>{availableAudits.length} jobs</span>
          </div>
          <div className="cards">
            {availableAudits.map((a) => (
              <AuditCard audit={a} key={a.id} />
            ))}
            {!availableAudits.length && (
              <p className="muted">No audit jobs are available right now.</p>
            )}
          </div>
        </section>
        <section className="queue">
          <div className="section-heading">
            <div>
              <p className="eyebrow">CURRENT JOBS</p>
              <h2>Assigned to you</h2>
            </div>
            <span>{currentAudits.length} jobs</span>
          </div>
          <div className="cards">
            {currentAudits.map((a) => (
              <AuditCard audit={a} key={a.id} />
            ))}
            {!currentAudits.length && (
              <p className="muted">
                Jobs you start will move here and are removed from the shared
                queue.
              </p>
            )}
          </div>
        </section>
        <section className="queue">
          <div className="section-heading">
            <div>
              <p className="eyebrow">COMPLETED TASKS</p>
              <h2>Submitted and paid work</h2>
            </div>
            <span>{completedAudits.length} jobs</span>
          </div>
          <div className="cards">
            {completedAudits.map((a) => (
              <AuditCard audit={a} completed key={a.id} />
            ))}
            {!completedAudits.length && (
              <p className="muted">
                Completed audit jobs will appear here after you submit them.
              </p>
            )}
          </div>
        </section>
      </section>
    </main>
  );
}

function Workspace({
  data,
  index,
  token,
  next,
  previous,
  back,
  refresh,
}: {
  data: WorkspaceData;
  index: number;
  token: string;
  next: () => void;
  previous: () => void;
  back: () => void;
  refresh: (decision: Decision) => void;
}) {
  const candidate = data.candidates[index];
  const saved = data.decisions.find((d) => d.candidateID === candidate.id);
  const [decision, setDecision] = useState(saved?.decision || "accurate");
  const [start, setStart] = useState(
    saved?.correctedStartSeconds ?? candidate.startSeconds,
  );
  const [end, setEnd] = useState(
    saved?.correctedEndSeconds ?? candidate.endSeconds,
  );
  const [category, setCategory] = useState(
    saved?.correctedCategoryID ?? candidate.categoryID,
  );
  const [notes, setNotes] = useState(saved?.notes || "");
  const [error, setError] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [audioURL, setAudioURL] = useState("");
  const [loadingAudio, setLoadingAudio] = useState(false);
  const [playbackSeconds, setPlaybackSeconds] = useState(0);
  const [clipDuration, setClipDuration] = useState(0);
  const audioRef = useRef<HTMLAudioElement>(null);
  useEffect(() => {
    const existing = data.decisions.find((d) => d.candidateID === candidate.id);
    setDecision(existing?.decision || "accurate");
    setStart(existing?.correctedStartSeconds ?? candidate.startSeconds);
    setEnd(existing?.correctedEndSeconds ?? candidate.endSeconds);
    setCategory(existing?.correctedCategoryID ?? candidate.categoryID);
    setNotes(existing?.notes || "");
    setError("");
    setAudioURL("");
    setPlaybackSeconds(0);
    setClipDuration(0);
  }, [candidate, data.decisions]);
  const listen = async () => {
    try {
      setLoadingAudio(true);
      setError("");
      const r = await fetch(
        `${API}/v1/internal/audits/${data.assignment.id}/segments/${candidate.id}/audio?asJson=true`,
        { headers: { Authorization: `Bearer ${token}` } },
      );
      if (!r.ok)
        throw new Error(
          "The review clip could not be prepared. Please try again.",
        );
      const value = await r.json();
      if (!value.url)
        throw new Error(
          "The review clip is temporarily unavailable. Please try again.",
        );
      setAudioURL(value.url);
      setPlaybackSeconds(0);
      setClipDuration(0);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoadingAudio(false);
    }
  };
  const save = async () => {
    try {
      setSubmitting(true);
      const r = await fetch(
        `${API}/v1/internal/audits/${data.assignment.id}/segments/${candidate.id}`,
        {
          method: "PUT",
          headers: {
            "Content-Type": "application/json",
            Authorization: `Bearer ${token}`,
          },
          body: JSON.stringify({
            decision,
            correctedCategoryID:
              decision === "wrong_category" ? category : null,
            correctedStartSeconds:
              decision === "adjust_timestamps" ? start : null,
            correctedEndSeconds: decision === "adjust_timestamps" ? end : null,
            notes,
          }),
        },
      );
      if (!r.ok)
        throw new Error("Your review could not be saved. Please try again.");
      refresh(await r.json());
      next();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSubmitting(false);
    }
  };
  const complete = async () => {
    try {
      setSubmitting(true);
      const r = await fetch(
        `${API}/v1/internal/audits/${data.assignment.id}/complete`,
        { method: "POST", headers: { Authorization: `Bearer ${token}` } },
      );
      if (!r.ok)
        throw new Error(
          "Review every event before submitting this audit to the admin team.",
        );
      back();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSubmitting(false);
    }
  };
  return (
    <main>
      <header>
        <button className="ghost" onClick={back}>
          ← Dashboard
        </button>
        <div>
          {index + 1} of {data.candidates.length}
        </div>
      </header>
      <section className="workspace">
        <aside>
          <p className="eyebrow">NOW AUDITING</p>
          <h2>{data.assignment.title}</h2>
          <p>
            {data.assignment.author}
            <br />
            {data.assignment.edition}
          </p>
          <p>
            <b>{data.assignment.reviewFocus}</b>
          </p>
          <div className="task-value">
            <span>Task value</span>
            <strong>{money(data.assignment.compensationAmount)}</strong>
            <small>Paid after your submitted work is approved.</small>
          </div>
          <progress
            value={data.decisions.length}
            max={data.candidates.length}
          />
          <small>
            {data.decisions.length} of {data.candidates.length} events checked
            off
          </small>
          <button
            disabled={
              submitting || data.decisions.length !== data.candidates.length
            }
            onClick={complete}
          >
            Submit completed audit
          </button>
          <small>
            When submitted, the admin team verifies your work and records the
            task value for manual payout.
          </small>
        </aside>
        <section className="review">
          <span className="pill">
            Event {index + 1} of {data.candidates.length}
          </span>
          <h1>{candidate.safeDescription}</h1>
          <p>{Math.round(candidate.confidence * 100)}% scanner confidence</p>
          <div className="audio">
            <strong>Listen and verify</strong>
            <p>
              Listen from <b>{timestamp(candidate.listenFromSeconds)}</b> to{" "}
              <b>{timestamp(candidate.listenToSeconds)}</b>. AudioChoice is
              expected to skip <b>{timestamp(candidate.startSeconds)}</b>–
              <b>{timestamp(candidate.endSeconds)}</b>.
            </p>
            {data.reviewMediaAvailable ? (
              <>
                {!audioURL && (
                  <button onClick={listen} disabled={loadingAudio}>
                    {loadingAudio
                      ? "Preparing secure clip…"
                      : "Play review clip"}
                  </button>
                )}
                {audioURL && (
                  <>
                    <audio
                      ref={audioRef}
                      controls
                      autoPlay
                      src={audioURL}
                      onLoadedMetadata={(event) =>
                        setClipDuration(event.currentTarget.duration || 0)
                      }
                      onTimeUpdate={(event) =>
                        setPlaybackSeconds(event.currentTarget.currentTime)
                      }
                    >
                      Your browser cannot play this review clip.
                    </audio>
                    <div
                      className="audit-timeline"
                      aria-label="Audiobook review timeline"
                    >
                      <div className="timeline-labels">
                        <span>{timestamp(candidate.listenFromSeconds)}</span>
                        <strong>
                          Now:{" "}
                          {timestamp(
                            candidate.listenFromSeconds + playbackSeconds,
                          )}
                        </strong>
                        <span>{timestamp(candidate.listenToSeconds)}</span>
                      </div>
                      <div className="timeline-track">
                        <div
                          className="timeline-skip-range"
                          style={{
                            left: `${Math.max(0, ((candidate.startSeconds - candidate.listenFromSeconds) / Math.max(1, candidate.listenToSeconds - candidate.listenFromSeconds)) * 100)}%`,
                            width: `${Math.min(100, ((candidate.endSeconds - candidate.startSeconds) / Math.max(1, candidate.listenToSeconds - candidate.listenFromSeconds)) * 100)}%`,
                          }}
                        />
                        <div
                          className="timeline-playhead"
                          style={{
                            left: `${Math.min(100, (playbackSeconds / Math.max(1, clipDuration || candidate.listenToSeconds - candidate.listenFromSeconds)) * 100)}%`,
                          }}
                        />
                      </div>
                      <input
                        className="timeline-scrubber"
                        type="range"
                        min="0"
                        max={
                          clipDuration ||
                          Math.max(
                            1,
                            candidate.listenToSeconds -
                              candidate.listenFromSeconds,
                          )
                        }
                        step="0.1"
                        value={Math.min(
                          playbackSeconds,
                          clipDuration ||
                            Math.max(
                              1,
                              candidate.listenToSeconds -
                                candidate.listenFromSeconds,
                            ),
                        )}
                        aria-label="Scrub review clip"
                        onChange={(event) => {
                          const nextTime = Number(event.currentTarget.value);
                          if (audioRef.current)
                            audioRef.current.currentTime = nextTime;
                          setPlaybackSeconds(nextTime);
                        }}
                      />
                      <small>
                        The green band is the current skip range. The marker
                        shows the exact audiobook time playing.
                      </small>
                    </div>
                  </>
                )}
              </>
            ) : (
              <p className="muted">
                This job is waiting for the administrator to attach its approved
                review source.
              </p>
            )}
          </div>
          <div className="decisions">
            {[
              ["accurate", "Correct skip"],
              ["adjust_timestamps", "Adjust skip range"],
              ["wrong_category", "Wrong category"],
              ["false_positive", "Should not skip"],
              ["needs_escalation", "Escalate"],
            ].map(([value, label]) => (
              <button
                className={decision === value ? "selected" : ""}
                onClick={() => setDecision(value)}
                key={value}
              >
                {label}
              </button>
            ))}
          </div>
          {decision === "adjust_timestamps" && (
            <div className="edit">
              <label>
                Corrected start (seconds)
                <input
                  type="number"
                  step="0.01"
                  value={start}
                  onChange={(e) => setStart(+e.target.value)}
                />
              </label>
              <label>
                Corrected end (seconds)
                <input
                  type="number"
                  step="0.01"
                  value={end}
                  onChange={(e) => setEnd(+e.target.value)}
                />
              </label>
            </div>
          )}
          {decision === "wrong_category" && (
            <label>
              Correct category
              <select
                value={category}
                onChange={(e) => setCategory(e.target.value)}
              >
                {data.categories.map((x) => (
                  <option value={x.id} key={x.id}>
                    {x.name}
                  </option>
                ))}
              </select>
            </label>
          )}
          <label>
            Correction suggestion or notes
            <textarea
              rows={4}
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
              placeholder="Example: Start the skip 5 seconds sooner, after the door closes."
            />
          </label>
          {error && <p className="error">{error}</p>}
          <footer>
            <button className="ghost" onClick={previous} disabled={index === 0}>
              Previous
            </button>
            <button disabled={submitting} onClick={save}>
              {submitting ? "Saving…" : "Save & next"}
            </button>
          </footer>
        </section>
      </section>
    </main>
  );
}
