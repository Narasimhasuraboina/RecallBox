(() => {
  "use strict";

  const converter = window.RecallBoxConverter;
  const form = document.querySelector("#convertForm");
  const fileInput = document.querySelector("#exportFile");
  const folderButton = document.querySelector("#chooseFolder");
  const folderName = document.querySelector("#folderName");
  const destinationHint = document.querySelector("#destinationHint");
  const convertButton = document.querySelector("#convertButton");
  const progressPanel = document.querySelector("#progressPanel");
  const progressTitle = document.querySelector("#progressTitle");
  const progressDetail = document.querySelector("#progressDetail");
  const progressBar = document.querySelector("#progressBar");
  const errorPanel = document.querySelector("#errorPanel");
  const resultPanel = document.querySelector("#resultPanel");
  const resultTitle = document.querySelector("#resultTitle");
  const resultLocation = document.querySelector("#resultLocation");
  const themeToggle = document.querySelector("#themeToggle");
  let outputDirectory = null;

  syncThemeToggle();
  themeToggle.addEventListener("click", () => {
    const nextTheme = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
    document.documentElement.dataset.theme = nextTheme;
    try { localStorage.setItem("recallbox-theme", nextTheme); } catch { /* Theme still applies for this page view. */ }
    syncThemeToggle();
  });

  const folderPickerAvailable = window.isSecureContext && typeof window.showDirectoryPicker === "function";
  if (!folderPickerAvailable) {
    folderButton.hidden = true;
    destinationHint.textContent = "Your browser will download one ZIP containing the structured folder. Choose its save location in the browser.";
  }

  folderButton.addEventListener("click", async () => {
    try {
      outputDirectory = await window.showDirectoryPicker({ id: "recallbox-output", mode: "readwrite" });
      folderName.textContent = `Selected folder: ${outputDirectory.name}`;
      folderName.hidden = false;
      destinationHint.textContent = "RecallBox will create a new Contact folder here and write the archive files directly.";
    } catch (error) {
      if (error.name !== "AbortError") showError(error.message || "The selected folder could not be opened.");
    }
  });

  fileInput.addEventListener("change", () => {
    const file = fileInput.files?.[0];
    document.querySelector("#fileName").textContent = file
      ? `${file.name} · ${formatBytes(file.size)}. The file is processed in this browser.`
      : "WhatsApp .txt/.zip, or a ZIP containing a .txt, .json, or .csv transcript. Maximum input size: 512 MB.";
    errorPanel.hidden = true;
    resultPanel.hidden = true;
  });

  form.addEventListener("submit", async event => {
    event.preventDefault();
    errorPanel.hidden = true;
    resultPanel.hidden = true;
    progressPanel.hidden = false;
    convertButton.disabled = true;
    folderButton.disabled = true;
    setProgress("Reading selected export", "No file data leaves this browser.", 2);
    try {
      const file = fileInput.files?.[0];
      const contact = document.querySelector("#contact").value;
      const parsed = await converter.readExport(file, (title, detail) => setProgress(title, detail, 12));
      const rootName = outputDirectory ? await uniqueContactFolder(outputDirectory, contact) : undefined;
      const archive = await converter.makeArchive(contact, file, parsed, (title, detail) => setProgress(title, detail, 35), rootName);
      if (outputDirectory) {
        await converter.writeToFolder(outputDirectory, archive, (title, detail, percent) => setProgress(title, detail, 60 + percent * 0.4));
        showResult(archive, `${outputDirectory.name}/${archive.rootFolder}`);
      } else {
        setProgress("Building downloadable archive", "Compressing chat files locally…", 70);
        const blob = await converter.createZip(archive, (title, detail) => setProgress(title, detail, 75));
        const zipName = `${archive.rootFolder}.zip`;
        downloadBlob(blob, zipName);
        showResult(archive, `${zipName} · check your browser’s Downloads or save dialog`);
      }
      progressPanel.hidden = true;
    } catch (error) {
      progressPanel.hidden = true;
      showError(error.message || "The export could not be converted.");
    } finally {
      convertButton.disabled = false;
      folderButton.disabled = false;
    }
  });

  function setProgress(title, detail, percent = 0) {
    progressTitle.textContent = title;
    progressDetail.textContent = detail || "";
    progressBar.style.width = `${Math.max(2, Math.min(100, percent))}%`;
  }

  function syncThemeToggle() {
    const isDark = document.documentElement.dataset.theme === "dark";
    themeToggle.setAttribute("aria-pressed", String(isDark));
    themeToggle.setAttribute("aria-label", `Switch to ${isDark ? "light" : "dark"} mode`);
    themeToggle.querySelector(".theme-icon").textContent = isDark ? "☀" : "☾";
    themeToggle.querySelector(".theme-label").textContent = isDark ? "Light mode" : "Dark mode";
  }

  async function uniqueContactFolder(parent, phone) {
    const digits = String(phone).replace(/\D/g, "");
    const base = `Contact-${digits}`;
    for (let suffix = 1; suffix < 10000; suffix++) {
      const candidate = suffix === 1 ? base : `${base} (${suffix})`;
      try {
        await parent.getDirectoryHandle(candidate);
      } catch (error) {
        if (error.name === "NotFoundError") return candidate;
        throw error;
      }
    }
    throw new Error("Could not choose a new archive folder name. Rename or move an existing Contact folder and try again.");
  }

  function showResult(archive, location) {
    resultTitle.textContent = `${archive.messages.length.toLocaleString()} messages converted`;
    const details = [`Saved to ${location}`, `${archive.mediaSaved} media files saved`, `${archive.itemsSkipped} items unavailable`];
    if (archive.warnings.length) details.push(`${archive.warnings.length} warning(s) in metadata.json`);
    resultLocation.textContent = details.join(" · ");
    resultPanel.hidden = false;
    resultPanel.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  function showError(message) {
    errorPanel.textContent = message;
    errorPanel.hidden = false;
  }

  function downloadBlob(blob, fileName) {
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 30000);
  }

  function formatBytes(value) {
    if (value < 1024) return `${value} B`;
    if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
    return `${(value / 1024 / 1024).toFixed(1)} MB`;
  }
})();
