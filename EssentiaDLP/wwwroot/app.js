const $ = (sel) => document.querySelector(sel);
const busy = $("#busy");
const logEl = $("#log");
let recoverId = null;
let lastLogLen = -1;

document.querySelectorAll("nav button").forEach((btn) => {
  btn.addEventListener("click", () => {
    document.querySelectorAll("nav button").forEach((b) => b.classList.remove("active"));
    btn.classList.add("active");
    document.querySelectorAll(".tab").forEach((t) => t.classList.add("hidden"));
    $(`#tab-${btn.dataset.tab}`).classList.remove("hidden");
    if (btn.dataset.tab === "failed") loadFailed();
    if (btn.dataset.tab === "settings") loadSettings();
  });
});

$("#galaxy-btn").addEventListener("click", async () => {
  const res = await fetch("/api/run/galaxy", { method: "POST" });
  if (res.status === 409) alert("Analysis is already running.");
});

$("#single-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const query = new FormData(e.target).get("query");
  const res = await fetch("/api/run/single", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ query }),
  });
  if (res.status === 409) alert("Analysis is already running.");
});

$("#settings-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const data = Object.fromEntries(new FormData(e.target).entries());
  data.maxSongs = Number(data.maxSongs);
  data.maxSongsPerMinute = Number(data.maxSongsPerMinute);
  const res = await fetch("/api/settings", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(data),
  });
  const msg = $("#settings-msg");
  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    msg.textContent = err.error || "Save failed";
    return;
  }
  msg.textContent = "Saved.";
});

$("#url-form").addEventListener("submit", async (e) => {
  const value = e.submitter?.value;
  if (value !== "ok" || !recoverId) return;
  const url = new FormData(e.target).get("url");
  const res = await fetch(`/api/failed/${encodeURIComponent(recoverId)}/url`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ url }),
  });
  if (res.status === 409) alert("Analysis is already running.");
});

async function loadSettings() {
  const s = await (await fetch("/api/settings")).json();
  const form = $("#settings-form");
  form.url.value = s.url || "";
  form.apiKey.value = s.apiKey || "";
  form.maxSongs.value = s.maxSongs ?? 100;
  form.maxSongsPerMinute.value = s.maxSongsPerMinute ?? 10;
  form.cron.value = s.cron || "";
}

async function loadFailed() {
  const items = await (await fetch("/api/failed")).json();
  const empty = $("#failed-empty");
  const table = $("#failed-table");
  const tbody = table.querySelector("tbody");
  tbody.innerHTML = "";
  if (!items.length) {
    empty.classList.remove("hidden");
    table.classList.add("hidden");
    return;
  }
  empty.classList.add("hidden");
  table.classList.remove("hidden");
  for (const item of items) {
    const tr = document.createElement("tr");
    const song = [item.artist, item.title || item.query].filter(Boolean).join(" — ");
    tr.innerHTML = `<td>${escapeHtml(song)}</td><td class="danger">${escapeHtml(item.error || "")}</td><td class="actions"></td>`;
    const actions = tr.querySelector(".actions");
    const upload = document.createElement("label");
    upload.textContent = "Upload";
    upload.className = "primary";
    const input = document.createElement("input");
    input.type = "file";
    input.accept = "audio/*,.mp3,.flac,.m4a,.ogg,.opus,.wav,.aac";
    input.hidden = true;
    input.addEventListener("change", async () => {
      if (!input.files[0]) return;
      const fd = new FormData();
      fd.append("file", input.files[0]);
      const res = await fetch(`/api/failed/${encodeURIComponent(item.id)}/upload`, { method: "POST", body: fd });
      if (res.status === 409) alert("Analysis is already running.");
    });
    upload.appendChild(input);
    upload.addEventListener("click", () => input.click());
    const fromUrl = document.createElement("button");
    fromUrl.textContent = "From URL";
    fromUrl.addEventListener("click", () => {
      recoverId = item.id;
      $("#url-form").url.value = "";
      $("#url-dialog").showModal();
    });
    const remove = document.createElement("button");
    remove.textContent = "Remove";
    remove.addEventListener("click", async () => {
      await fetch(`/api/failed/${encodeURIComponent(item.id)}`, { method: "DELETE" });
      loadFailed();
    });
    actions.append(upload, fromUrl, remove);
    tbody.appendChild(tr);
  }
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

async function poll() {
  try {
    const s = await (await fetch("/api/status")).json();
    busy.textContent = s.running ? `Running (${s.mode || "job"})` : "Idle";
    busy.className = "pill " + (s.running ? "run" : "idle");
    const lines = (s.logs || []).join("\n");
    if (lines.length !== lastLogLen) {
      lastLogLen = lines.length;
      logEl.textContent = lines;
      logEl.scrollTop = logEl.scrollHeight;
    }
  } catch { }
}

loadSettings();
poll();
setInterval(poll, 1000);
