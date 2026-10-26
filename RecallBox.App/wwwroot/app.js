const tokenKey = "recallbox-lan-token";
const tokenGate = document.querySelector("#tokenGate");
const tokenInput = document.querySelector("#lanToken");
let status = null;
let pollTimer = null;

function headers(json = false) {
  const result = {};
  const token = localStorage.getItem(tokenKey);
  if (token) result.Authorization = `Bearer ${token}`;
  if (json) result["Content-Type"] = "application/json";
  return result;
}

async function api(path, options = {}) {
  const response = await fetch(path, { ...options, headers: { ...headers(!!options.body), ...(options.headers || {}) } });
  if (response.status === 401) {
    tokenGate.hidden = false;
    throw new Error("LAN access token required.");
  }
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || `Request failed (${response.status}).`);
  return data;
}

function showError(message) {
  const target = document.querySelector("#formError");
  target.textContent = message;
  target.hidden = false;
}

async function loadStatus() {
  try {
    status = await api("/api/status");
    document.querySelector("#serverDot").classList.add("ready");
    document.querySelector("#serverText").textContent = status.lanEnabled ? "Private network enabled" : "Local only";
    const scanAvailable = status.chatScanAvailable;
    document.querySelector("#sourceTitle").textContent = !status.whatsappFound ? "WhatsApp Desktop not detected" :
      status.whatsapp.accessibleMessageControls > 0 ? "WhatsApp Desktop exposes message controls" :
        status.whatsapp.ocrFallbackAvailable ? "Visible-window OCR fallback is available" : "WhatsApp messages aren’t accessible";
    document.querySelector("#sourceHint").textContent = !status.whatsappFound
      ? "WhatsApp Desktop is not already running. RecallBox will not open it. You can import an official export below without launching WhatsApp on this PC."
      : status.whatsapp.accessibleMessageControls > 0
        ? `RecallBox found ${status.whatsapp.accessibleMessageControls} accessible controls. It will read only the already-open chat and will not focus, search, or scroll WhatsApp.`
        : status.whatsapp.ocrFallbackAvailable
          ? "RecallBox uses local OCR on the existing WhatsApp window without bringing it forward or changing chats. It captures only the current view; use a phone export ZIP for full available history and media."
          : "This WhatsApp window exposes no readable message controls and local OCR is unavailable. Use the official export importer below.";
    const downloadButton = document.querySelector("#downloadButton");
    downloadButton.disabled = !scanAvailable;
    if (!scanAvailable) downloadButton.title = "This WhatsApp window does not expose readable chat messages. Use the official export importer.";
    document.querySelector("#destination").value = status.preferredFolder || "";
    await loadJobs();
  } catch (error) {
    if (!String(error.message).includes("token required")) {
      document.querySelector("#serverText").textContent = "Server unavailable";
      document.querySelector("#sourceTitle").textContent = "Start RecallBox on Windows";
    }
  }
}

async function loadJobs() {
  const jobs = await api("/api/jobs");
  const container = document.querySelector("#jobs");
  if (!jobs.length) {
    container.innerHTML = '<div class="empty">Your downloads and their progress will appear here.</div>';
    return;
  }
  container.innerHTML = jobs.map(job => {
    const terminal = ["completed-with-limitations", "failed", "cancelled-with-checkpoint"].includes(job.status);
    const progress = terminal ? 100 : Math.min(94, Math.max(5, job.pagesRead * 5));
    const warnings = (job.warnings || []).slice(0, 3).map(value => `<div class="job-warning">${escapeHtml(value)}</div>`).join("");
    const errors = (job.errors || []).slice(0, 2).map(value => `<div class="job-error">${escapeHtml(value)}</div>`).join("");
    const retry = job.status === "failed" || job.status === "cancelled-with-checkpoint"
      ? `<button data-retry="${escapeHtml(job.id)}">${job.status === "cancelled-with-checkpoint" ? "Resume chat scan" : job.acquisitionMethod?.includes("export") ? "Retry saved export" : "Retry chat scan"}</button>` : "";
    const cancel = ["running", "queued"].includes(job.status) && !job.acquisitionMethod?.includes("export")
      ? `<button class="secondary" data-cancel="${escapeHtml(job.id)}">Cancel and save progress</button>` : "";
    const resultPath = job.resultPath ? `<span>${escapeHtml(job.resultPath)}</span>` : "";
    const button = job.status === "running" || job.status === "queued" ? "" : retry;
    return `<article class="job"><div class="job-top"><div><h3>${escapeHtml(job.phoneNumber)}</h3><small>${escapeHtml(job.contactFolder)} · ${escapeHtml(job.acquisitionMethod || "UI scan")}</small></div><span class="status ${job.status === "failed" ? "failed" : ""}">${escapeHtml(job.status)}</span></div><div class="progress-track"><span style="width:${progress}%"></span></div><div class="job-stats"><span>${job.messagesSaved} messages saved</span><span>${job.attachmentsSaved} attachments saved</span><span>${job.itemsSkipped} items skipped</span><span>${job.pagesRead} views read</span></div><div class="job-phase">${escapeHtml(job.phase)} · coverage ${escapeHtml(job.coverage)}</div>${warnings}${errors}<div class="job-actions">${resultPath}${button}${cancel}</div></article>`;
  }).join("");
  container.querySelectorAll("[data-retry]").forEach(button => button.addEventListener("click", async () => {
    button.disabled = true;
    try { await api(`/api/jobs/${button.dataset.retry}/retry`, { method: "POST" }); await loadJobs(); }
    catch (error) { alert(error.message); }
  }));
  container.querySelectorAll("[data-cancel]").forEach(button => button.addEventListener("click", async () => {
    button.disabled = true;
    try { await api(`/api/jobs/${button.dataset.cancel}/cancel`, { method: "POST" }); await loadJobs(); }
    catch (error) { alert(error.message); button.disabled = false; }
  }));
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char]);
}

document.querySelector("#downloadForm").addEventListener("submit", async event => {
  event.preventDefault();
  const button = document.querySelector("#downloadButton");
  document.querySelector("#formError").hidden = true;
  button.disabled = true;
  try {
    const destinationFolder = document.querySelector("#destination").value.trim();
    const settings = await api("/api/settings", { method: "POST", body: JSON.stringify({ preferredFolder: destinationFolder }) });
    const request = {
      phoneNumber: document.querySelector("#phone").value,
      destinationFolder: settings.preferredFolder,
      confirmedOwnChat: document.querySelector("#ownChat").checked
    };
    await api("/api/download", { method: "POST", body: JSON.stringify(request) });
    document.querySelector("#phone").value = "";
    document.querySelector("#ownChat").checked = false;
    await loadJobs();
  } catch (error) { showError(error.message); }
  finally { button.disabled = false; }
});

document.querySelector("#browseButton").addEventListener("click", async () => {
  try {
    const result = await api("/api/browse", { method: "POST" });
    if (!result.cancelled) document.querySelector("#destination").value = result.path;
  } catch (error) { showError(error.message); }
});

document.querySelector("#importForm").addEventListener("submit", event => {
  event.preventDefault();
  const button = document.querySelector("#importButton");
  const error = document.querySelector("#importError");
  const progress = document.querySelector("#uploadProgress");
  error.hidden = true;
  progress.hidden = false;
  button.disabled = true;
  const form = new FormData();
  form.append("phoneNumber", document.querySelector("#importPhone").value);
  form.append("destinationFolder", document.querySelector("#destination").value.trim() || status?.preferredFolder || "");
  form.append("confirmedOwnChat", String(document.querySelector("#importOwnChat").checked));
  form.append("file", document.querySelector("#exportFile").files[0]);
  const xhr = new XMLHttpRequest();
  xhr.open("POST", "/api/import");
  const token = localStorage.getItem(tokenKey);
  if (token) xhr.setRequestHeader("Authorization", `Bearer ${token}`);
  xhr.upload.addEventListener("progress", event => {
    if (!event.lengthComputable) return;
    const percent = Math.round((event.loaded / event.total) * 100);
    document.querySelector("#uploadProgressBar").style.width = `${percent}%`;
    document.querySelector("#uploadProgressText").textContent = percent < 100 ? `Sending export to this Windows computer: ${percent}%` : "Export received; parsing locally…";
  });
  xhr.addEventListener("load", async () => {
    try {
      const result = JSON.parse(xhr.responseText || "{}");
      if (xhr.status < 200 || xhr.status >= 300) throw new Error(result.error || `Import failed (${xhr.status}).`);
      await loadJobs();
      document.querySelector("#importPhone").value = "";
      document.querySelector("#exportFile").value = "";
      document.querySelector("#importOwnChat").checked = false;
      document.querySelector("#uploadProgressText").textContent = "Export received; archive processing is running below.";
    } catch (e) { error.textContent = e.message; error.hidden = false; }
    finally { button.disabled = false; }
  });
  xhr.addEventListener("error", () => { error.textContent = "Upload failed. Check the local server connection and try again."; error.hidden = false; button.disabled = false; });
  xhr.send(form);
});

document.querySelector("#refreshButton").addEventListener("click", loadJobs);
document.querySelector("#saveToken").addEventListener("click", async () => {
  localStorage.setItem(tokenKey, tokenInput.value.trim());
  tokenGate.hidden = true;
  await loadStatus();
});
tokenGate.addEventListener("click", event => { if (event.target === tokenGate) tokenInput.focus(); });
loadStatus();
pollTimer = setInterval(() => loadJobs().catch(() => {}), 1600);
