// Shared helpers for the Line PC Server pages (no external libraries - works on a closed network).

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function fmtTime(d) { return d ? new Date(d).toLocaleTimeString([], { hour12: false }) : "-"; }
function fmtDateTime(d) {
  if (!d) return "-";
  const x = new Date(d);
  return x.toLocaleDateString([], { year: "numeric", month: "2-digit", day: "2-digit" }) + " " + x.toLocaleTimeString([], { hour12: false });
}
function ago(d) {
  if (!d) return "never";
  const s = Math.round((Date.now() - new Date(d)) / 1000);
  if (s < 60) return s + " s ago";
  if (s < 3600) return Math.round(s / 60) + " min ago";
  return fmtDateTime(d);
}

/** fetch wrapper: returns { ok, status, data } and never throws. */
async function api(method, url, body) {
  try {
    const r = await fetch(url, {
      method,
      headers: body !== undefined ? { "Content-Type": "application/json" } : {},
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    let data = null;
    const text = await r.text();
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }
    return { ok: r.ok, status: r.status, data };
  } catch (e) {
    return { ok: false, status: 0, data: { error: "Server not reachable: " + e.message } };
  }
}
function errText(res) {
  const d = res.data || {};
  if (Array.isArray(d.errors)) return d.errors.join("  •  ");
  return d.error || d.message || (typeof d === "string" ? d : "") || `HTTP ${res.status}`;
}

/**
 * Live updates. Calls onChange(lineId) - "*" means "reload everything".
 * Shows connection state in an element with id "conn" if present.
 */
function live(onChange) {
  let es, retry;
  const conn = document.getElementById("conn");
  const set = (ok) => { if (conn) { conn.textContent = ok ? "Live" : "Connection lost - retrying…"; conn.className = "conn" + (ok ? "" : " lost"); } };
  const open = () => {
    es = new EventSource("/api/events");
    es.onopen = () => { set(true); onChange("*"); };
    es.onmessage = (e) => onChange(e.data);
    es.onerror = () => { set(false); es.close(); clearTimeout(retry); retry = setTimeout(open, 3000); };
  };
  open();
}

/** Run fn at most once per `ms` (collapses bursts of live updates). */
function throttle(fn, ms = 300) {
  let t = null, pending = false;
  return (...a) => {
    if (t) { pending = true; return; }
    fn(...a);
    t = setTimeout(() => { t = null; if (pending) { pending = false; fn(...a); } }, ms);
  };
}

/** Short warning sound made in the browser (no sound file needed). Works after the user clicked once on the page. */
let audioCtx = null;
function unlockAudio() {
  try { audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)(); if (audioCtx.state === "suspended") audioCtx.resume(); } catch { }
}
document.addEventListener("click", unlockAudio);
document.addEventListener("keydown", unlockAudio);
function beep(times = 3) {
  if (!audioCtx) return;
  for (let i = 0; i < times; i++) {
    const o = audioCtx.createOscillator(), g = audioCtx.createGain();
    o.type = "square"; o.frequency.value = 880;
    const t = audioCtx.currentTime + i * 0.35;
    g.gain.setValueAtTime(0.0001, t); g.gain.exponentialRampToValueAtTime(0.25, t + 0.02); g.gain.exponentialRampToValueAtTime(0.0001, t + 0.25);
    o.connect(g).connect(audioCtx.destination); o.start(t); o.stop(t + 0.27);
  }
}

/** Simple modal. buttons: [{ text, cls, value }]. Resolves with the clicked button's value (or null on close). */
function modal(title, html, buttons = [{ text: "Close", value: null }]) {
  return new Promise(resolve => {
    const back = document.createElement("div");
    back.className = "modal-back";
    back.innerHTML = `<div class="modal" role="dialog" aria-modal="true"><h3>${esc(title)}</h3><div class="body">${html}</div><div class="foot"></div></div>`;
    const foot = back.querySelector(".foot");
    const close = (v) => { back.remove(); document.removeEventListener("keydown", onKey); resolve(v); };
    const onKey = (e) => { if (e.key === "Escape") close(null); };
    buttons.forEach((b, i) => {
      const btn = document.createElement("button");
      btn.textContent = b.text;
      if (b.cls) btn.className = b.cls;
      btn.onclick = () => close(b.value);
      foot.appendChild(btn);
      if (i === buttons.length - 1) setTimeout(() => btn.focus(), 0);
    });
    back.addEventListener("click", (e) => { if (e.target === back) close(null); });
    document.addEventListener("keydown", onKey);
    document.body.appendChild(back);
  });
}

function setNavActive(name) {
  $$(".topbar nav a").forEach(a => a.classList.toggle("active", a.dataset.nav === name));
}
