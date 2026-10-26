(function (root, factory) {
  const converter = factory(root);
  if (typeof module === "object" && module.exports) module.exports = converter;
  else root.RecallBoxConverter = converter;
})(typeof globalThis !== "undefined" ? globalThis : this, function (root) {
  "use strict";

  const MAX_INPUT_BYTES = 512 * 1024 * 1024;
  const MAX_TRANSCRIPT_BYTES = 128 * 1024 * 1024;
  const MAX_EXPANDED_BYTES = 2 * 1024 * 1024 * 1024;
  const MAX_ZIP_ENTRIES = 20000;
  const mediaExtensions = new Set(["jpg", "jpeg", "png", "gif", "webp", "heic", "mp4", "mov", "3gp", "mkv", "opus", "m4a", "mp3", "aac", "wav", "ogg", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "vcf", "txt", "zip"]);

  function parseStart(line) {
    const android = /^\u200e?(\d{1,4}[/-]\d{1,2}[/-]\d{1,4},?\s+\d{1,2}:\d{2}(?::\d{2})?(?:\s*[AP]M\.?)?)\s+-\s*(.*)$/i.exec(line);
    if (android) return { timestamp: clean(android[1]), rest: android[2] };
    const bracketed = /^\u200e?\[([^\]]{8,80})\]\s*(.*)$/.exec(line);
    if (bracketed) return { timestamp: clean(bracketed[1]), rest: bracketed[2] };
    const iso = /^\[?(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)\]?\s*[|,-]\s*(.*)$/i.exec(line);
    if (iso) return { timestamp: clean(iso[1]), rest: iso[2] };
    return null;
  }

  function splitSender(rest) {
    const value = clean(rest).replace(/^[\u200e\u200f\u202a\u202c\u2066\u2069]+/, "");
    const match = /^([^:\r\n]{1,120}):\s?([\s\S]*)$/.exec(value);
    return match ? { sender: clean(match[1]) || null, text: match[2] } : { sender: null, text: value };
  }

  function parseText(text) {
    const messages = [];
    let current = null;
    for (const line of text.replace(/^\ufeff/, "").split(/\r?\n/)) {
      const start = parseStart(line);
      if (start) {
        if (current) messages.push(current);
        const parts = splitSender(start.rest);
        current = { timestampText: start.timestamp, sender: parts.sender, text: parts.text, mediaRefs: [] };
        continue;
      }
      if (current) current.text += `\n${line}`;
    }
    if (current) messages.push(current);
    for (const message of messages) message.mediaRefs = getTextMediaReferences(message.text);
    return messages;
  }

  function getTextMediaReferences(text) {
    const refs = [];
    const named = /<attached:\s*([^>]+)>|([^\r\n<>]{1,180}\.(?:jpe?g|png|gif|webp|heic|mp4|mov|3gp|m4a|opus|mp3|aac|pdf|docx?|xlsx?|pptx?|zip|vcf))\s*\(file attached\)/gi;
    for (const match of text.matchAll(named)) refs.push({ name: clean(match[1] || match[2]), omitted: false });
    if (refs.length === 0 && /<media omitted>|\(file attached\)/i.test(text)) refs.push({ name: "", omitted: true });
    return refs;
  }

  function parseJson(text) {
    const data = JSON.parse(text);
    let rows = Array.isArray(data) ? data :
      data?.messages ?? data?.chat?.messages ?? data?.conversation?.messages ?? data?.items ?? data?.chats?.[0]?.messages;
    if (!Array.isArray(rows)) throw new Error("JSON transcript must be an array or contain a messages/items list.");
    const messages = [];
    for (const row of rows) {
      if (row === null || typeof row !== "object") continue;
      const body = row.text ?? row.body ?? row.message ?? row.content ?? row.messageText;
      if (body === undefined || body === null || typeof body === "object") continue;
      const senderValue = row.sender ?? row.author ?? row.from ?? row.user ?? row.name;
      const timestamp = row.timestamp ?? row.dateTime ?? row.datetime ?? row.date ?? row.time ?? row.createdAt ?? row.created_at ?? row.sentAt;
      messages.push({
        timestampText: normalizeTimestamp(timestamp),
        sender: senderName(senderValue),
        text: String(body),
        mediaRefs: collectJsonMedia(row)
      });
    }
    return messages;
  }

  function collectJsonMedia(row) {
    const values = [row.attachments, row.media, row.files, row.attachment].flatMap(value => value === undefined || value === null ? [] : Array.isArray(value) ? value : [value]);
    return values.map(value => {
      if (typeof value === "string") return { name: value, omitted: false };
      if (!value || typeof value !== "object") return null;
      const name = value.fileName ?? value.filename ?? value.name ?? value.path ?? value.url;
      return name ? { name: String(name), omitted: false } : null;
    }).filter(Boolean);
  }

  function parseCsv(text) {
    const rows = readCsv(text);
    if (rows.length < 2) throw new Error("CSV needs a header row and at least one message row.");
    const headers = rows[0].map(value => value.toLowerCase().replace(/[^a-z0-9]/g, ""));
    const column = names => headers.findIndex(name => names.includes(name));
    const timeColumn = column(["timestamp", "datetime", "date", "time", "createdat", "sentat"]);
    const senderColumn = column(["sender", "author", "from", "user", "name"]);
    const textColumn = column(["message", "text", "body", "content", "messagetext"]);
    const attachmentColumn = column(["attachment", "attachments", "media", "file", "files", "filename"]);
    if (textColumn < 0) throw new Error("CSV transcript needs a message, text, body, or content column.");
    return rows.slice(1).filter(row => row.some(value => value.trim())).map(row => ({
      timestampText: timeColumn >= 0 ? row[timeColumn] || null : null,
      sender: senderColumn >= 0 ? clean(row[senderColumn]) || null : null,
      text: row[textColumn] || "",
      mediaRefs: attachmentColumn >= 0 && row[attachmentColumn]
        ? row[attachmentColumn].split(/[;|]/).map(name => ({ name: clean(name), omitted: false })).filter(item => item.name)
        : []
    })).filter(message => message.text.length > 0);
  }

  function readCsv(text) {
    const rows = [];
    let row = [], cell = "", quoted = false;
    for (let i = 0; i < text.length; i++) {
      const char = text[i];
      if (quoted) {
        if (char === '"' && text[i + 1] === '"') { cell += '"'; i++; }
        else if (char === '"') quoted = false;
        else cell += char;
      } else if (char === '"') quoted = true;
      else if (char === ",") { row.push(cell); cell = ""; }
      else if (char === "\n") { row.push(cell.replace(/\r$/, "")); rows.push(row); row = []; cell = ""; }
      else cell += char;
    }
    if (cell.length || row.length) { row.push(cell.replace(/\r$/, "")); rows.push(row); }
    return rows;
  }

  function senderName(value) {
    if (value === undefined || value === null) return null;
    if (typeof value === "object") value = value.name ?? value.displayName ?? value.phone ?? value.id;
    return value === undefined || value === null || String(value).trim() === "" ? null : String(value).trim();
  }

  function normalizeTimestamp(value) {
    if (value === undefined || value === null || value === "") return null;
    if (typeof value === "number" && Number.isFinite(value)) {
      const milliseconds = value > 100000000000 ? value : value * 1000;
      const date = new Date(milliseconds);
      return Number.isNaN(date.getTime()) ? String(value) : date.toISOString();
    }
    return String(value);
  }

  async function readExport(file, onProgress = () => {}) {
    if (!file) throw new Error("Choose a chat export file.");
    if (file.size > MAX_INPUT_BYTES) throw new Error("This file is larger than RecallBox’s 512 MB browser limit. Export a smaller chat or use the text-only option.");
    const extension = file.name.split(".").pop().toLowerCase();
    if (extension === "zip") return readZip(file, onProgress);
    if (!["txt", "json", "csv"].includes(extension)) throw new Error("Choose a WhatsApp .txt/.zip export or a supported .txt, .json, or .csv transcript.");
    if (file.size > MAX_TRANSCRIPT_BYTES) throw new Error("The transcript is larger than the 128 MB text limit for browser conversion.");
    onProgress("Reading transcript", file.name);
    const text = await file.text();
    const parsed = parseTranscript(file.name, text);
    if (!parsed.messages.length) throw new Error("No supported timestamped messages were found in this file.");
    return { messages: parsed.messages, mediaEntries: [], warnings: addTimestampWarnings(parsed.messages, parsed.warnings), sourceName: file.name, sourceKind: extension };
  }

  async function readZip(file, onProgress) {
    if (!root.JSZip) throw new Error("The local ZIP reader did not load. Refresh the page and try again.");
    onProgress("Opening ZIP locally", file.name);
    const zip = await root.JSZip.loadAsync(await file.arrayBuffer(), { checkCRC32: false, createFolders: false });
    const entries = Object.values(zip.files).filter(entry => !entry.dir);
    if (entries.length > MAX_ZIP_ENTRIES) throw new Error("This ZIP has too many files to process safely (maximum 20,000).");
    const expandedBytes = entries.reduce((sum, entry) => sum + (entry._data?.uncompressedSize || 0), 0);
    if (expandedBytes > MAX_EXPANDED_BYTES) throw new Error("This ZIP expands beyond the 2 GB browser safety limit.");
    const candidates = entries.filter(entry => /\.(txt|json|csv)$/i.test(entry.name) && (entry._data?.uncompressedSize || 0) <= MAX_TRANSCRIPT_BYTES);
    if (!candidates.length) throw new Error("No supported .txt, .json, or .csv transcript was found in this ZIP.");
    candidates.sort((a, b) => transcriptScore(b) - transcriptScore(a) || (b._data?.uncompressedSize || 0) - (a._data?.uncompressedSize || 0));
    let selected = null;
    const recognized = [];
    for (let i = 0; i < Math.min(candidates.length, 32); i++) {
      onProgress("Finding chat transcript", candidates[i].name);
      try {
        const parsed = parseTranscript(candidates[i].name, await candidates[i].async("string"));
        if (parsed.messages.length) {
          recognized.push({ entry: candidates[i], ...parsed });
        }
      } catch { /* Other text files in the ZIP are not chat transcripts. */ }
    }
    if (!recognized.length) throw new Error("The ZIP contains text files, but none matched a supported chat transcript format.");
    recognized.sort((a, b) => b.messages.length - a.messages.length || transcriptScore(b.entry) - transcriptScore(a.entry));
    selected = recognized[0];
    const warnings = addTimestampWarnings(selected.messages, selected.warnings);
    if (recognized.length > 1) warnings.push(`The ZIP contains multiple supported transcripts; “${selected.entry.name}” was selected because it had the most recognized messages.`);
    const mediaEntries = entries.filter(entry => entry !== selected.entry && !candidates.includes(entry));
    if (!mediaEntries.length && selected.messages.some(message => message.mediaRefs.length)) warnings.push("The transcript references media, but the ZIP contains no matching media files.");
    return { messages: selected.messages, mediaEntries, warnings, sourceName: file.name, transcriptName: selected.entry.name, sourceKind: "zip" };
  }

  function transcriptScore(entry) {
    const name = entry.name.toLowerCase();
    const extension = name.split(".").pop();
    return (/(whatsapp|chat|conversation|message|transcript)/.test(name) ? 100 : 0) + (extension === "txt" ? 12 : extension === "json" ? 8 : 4);
  }

  function addTimestampWarnings(messages, warnings) {
    const result = [...warnings];
    if (messages.some(message => !message.timestampText || !parseLocalTimestamp(message.timestampText)))
      result.push("Some messages have no usable full date/time or an ambiguous timestamp. Original timestamp text is preserved; unknown dates are not guessed.");
    return [...new Set(result)];
  }

  function parseTranscript(name, text) {
    const extension = name.split(".").pop().toLowerCase();
    if (extension === "json") return { messages: parseJson(text), warnings: [] };
    if (extension === "csv") return { messages: parseCsv(text), warnings: [] };
    const messages = parseText(text);
    const warnings = [];
    const timestampCount = messages.filter(message => message.timestampText && parseLocalTimestamp(message.timestampText)).length;
    if (messages.length && timestampCount < messages.length) warnings.push("Some message timestamps could not be parsed as unambiguous dates. Original timestamp text is preserved.");
    if (messages.some(message => /<media omitted>|\(file attached\)/i.test(message.text))) warnings.push("The transcript marks media that may be missing from the export. Missing files are listed on their messages.");
    return { messages, warnings };
  }

  function normalizeMessages(messages) {
    return messages.map((message, index) => ({
      id: `export-${String(index + 1).padStart(8, "0")}`,
      sequence: index,
      sourcePageNumber: 0,
      text: message.text,
      sender: message.sender,
      timestampText: message.timestampText,
      localTimestamp: message.timestampText ? parseLocalTimestamp(message.timestampText) : null,
      replyOrEditMetadata: /\b(edited|replied|replying to|quoted)\b/i.test(message.text) ? message.text : null,
      messageType: message.mediaRefs?.length ? "attachment" : "text",
      sourceControlType: "Local chat export",
      sourceAutomationId: "",
      media: [],
      limitation: null,
      mediaRefs: message.mediaRefs || []
    }));
  }

  async function attachMedia(messages, entries, onProgress = () => {}) {
    const byName = new Map();
    for (const entry of entries) {
      const name = baseName(entry.name);
      if (!name) continue;
      const key = name.toLowerCase();
      if (!byName.has(key)) byName.set(key, []);
      byName.get(key).push(entry);
    }
    const nameCounts = new Map();
    const outputFiles = [];
    let saved = 0, skipped = 0, referenceCount = 0;
    for (const message of messages) referenceCount += message.mediaRefs.length;
    let processed = 0;
    for (const message of messages) {
      for (const ref of message.mediaRefs) {
        processed++;
        onProgress("Matching media files", `${processed} of ${referenceCount}`);
        const requested = baseName(ref.name || "");
        const queue = requested ? byName.get(requested.toLowerCase()) : null;
        const entry = queue?.shift();
        if (!entry) {
          const category = mediaCategory(requested || message.text);
          const limitation = ref.omitted ? "WhatsApp marked this attachment as omitted from the export." : "The export references a file that was not present in the selected ZIP.";
          message.media.push({ category, fileName: requested || null, relativePath: null, downloaded: false, limitation });
          message.limitation = "One or more attachments are unavailable in the selected export.";
          message.messageType = "attachment-with-gaps";
          skipped++;
          continue;
        }
        const category = mediaCategory(entry.name);
        const uniqueName = uniqueMediaName(baseName(entry.name), category, nameCounts);
        const relativePath = `media/${category}/${uniqueName}`;
        message.media.push({ category, fileName: uniqueName, relativePath, downloaded: true, limitation: null });
        outputFiles.push({ path: relativePath, entry });
        saved++;
      }
      delete message.mediaRefs;
    }
    return { outputFiles, saved, skipped };
  }

  function createMetadata(contact, file, messages, media, warnings, transcriptName) {
    const moreWarnings = [...warnings];
    if (file.name.toLowerCase().endsWith(".txt") && media.skipped === 0) moreWarnings.push("A text-only export was supplied; no media files were available to import.");
    return {
      contactIdentifier: contact,
      sourceFile: file.name,
      transcriptFile: transcriptName || file.name,
      processedAt: new Date().toISOString(),
      retrievalStatus: "completed-with-limitations",
      coverage: "source-export-file",
      processingLocation: "This browser on your device; no chat content was uploaded.",
      messagesFound: messages.length,
      attachmentsSaved: media.saved,
      itemsSkipped: media.skipped,
      source: "Local chat export converter",
      knownLimitations: [
        "The archive includes only records and media present in the selected export. It does not establish complete account history.",
        "Date order is preserved from the selected transcript; ambiguous timestamps retain their original text and are not assigned a date."
      ],
      warnings: [...new Set(moreWarnings)],
      errors: []
    };
  }

  function buildText(messages) {
    return messages.map(message => {
      const lines = [`[${message.timestampText || "time unavailable"}] ${message.sender || "sender unavailable"}: ${message.text}`];
      for (const media of message.media) lines.push(`    ${media.downloaded ? "attachment saved" : "attachment unavailable"}: ${media.relativePath || media.fileName || media.limitation}`);
      return lines.join("\n");
    }).join("\n") + "\n";
  }

  function buildHtml(messages, contact) {
    const body = [];
    let lastDay = null;
    for (const message of messages) {
      const day = message.localTimestamp ? message.localTimestamp.slice(0, 10) : "Date unavailable";
      if (day !== lastDay) { body.push(`<div class="day">${escapeHtml(day)}</div>`); lastDay = day; }
      const mediaHtml = message.media.map(media => {
        if (!media.downloaded || !media.relativePath) return `<div class="media unavailable">${escapeHtml(media.limitation || "Attachment unavailable")}${media.fileName ? `: ${escapeHtml(media.fileName)}` : ""}</div>`;
        const href = media.relativePath.split("/").map(encodeURIComponent).join("/");
        if (media.category === "images") return `<div class="media"><a href="${href}"><img loading="lazy" src="${href}" alt="Chat image"></a></div>`;
        if (media.category === "videos") return `<div class="media"><video controls preload="metadata" src="${href}"></video></div>`;
        if (media.category === "audio") return `<div class="media"><audio controls preload="metadata" src="${href}"></audio></div>`;
        return `<div class="media"><a href="${href}">${escapeHtml(media.fileName || "Open attachment")}</a></div>`;
      }).join("");
      body.push(`<article class="message"><b class="sender">${escapeHtml(message.sender || "Sender unavailable")}</b><div class="text">${escapeHtml(message.text)}</div><div class="time">${escapeHtml(message.timestampText || "Timestamp unavailable")}</div>${mediaHtml}</article>`);
    }
    const notice = "Local archive generated from the supplied chat export. It does not prove complete account history.";
    return `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Chat archive — ${escapeHtml(contact)}</title><style>body{font:15px/1.5 system-ui,sans-serif;margin:0;background:#edf2ef;color:#192b25}.wrap{max-width:880px;margin:auto;padding:24px}.notice{padding:12px 14px;background:#fff3d0;border-radius:11px;color:#62522a;font-size:12px;margin:12px 0 20px}.day{width:max-content;margin:22px auto 10px;padding:5px 11px;border-radius:20px;background:#dcebe2;font-size:11px;color:#355c48}.message{max-width:78%;margin:8px 0;padding:11px 14px;border-radius:12px;background:#fff;box-shadow:0 1px 2px #0001;white-space:pre-wrap;overflow-wrap:anywhere}.sender{display:block;color:#17644b;font-size:12px;margin-bottom:4px}.time{margin-top:8px;text-align:right;color:#738078;font-size:10px}.media{margin-top:8px;color:#79612b;font-size:11px}.media img,.media video{display:block;max-width:100%;max-height:400px;border-radius:8px}.media audio{max-width:100%}.media a{color:#17644b}.unavailable{padding:7px;background:#fff8e6;border-radius:7px}@media(max-width:600px){.wrap{padding:13px}.message{max-width:94%}}</style></head><body><main class="wrap"><h1>Chat archive</h1><div class="notice">${escapeHtml(notice)}</div>${body.join("\n")}</main></body></html>`;
  }

  function makeArchive(contactInput, file, parsed, onProgress = () => {}, requestedFolderName) {
    const digits = String(contactInput).replace(/\D/g, "");
    if (digits.length < 7 || digits.length > 15) throw new Error("Enter a valid phone number with country code (7–15 digits).");
    const contact = `+${digits}`;
    const messages = normalizeMessages(parsed.messages);
    return attachMedia(messages, parsed.mediaEntries, onProgress).then(media => {
      const metadata = createMetadata(contact, file, messages, media, parsed.warnings, parsed.transcriptName);
      const json = {
        contactIdentifier: contact,
        acquisitionMethod: "Imported chat export processed locally in the browser",
        ordering: "Source transcript order; timestamps are preserved as provided by the export.",
        messages
      };
      const rootFolder = requestedFolderName || `Contact-${digits}`;
      const outputFiles = [
        ...media.outputFiles.map(item => ({ path: `${rootFolder}/${item.path}`, entry: item.entry })),
        { path: `${rootFolder}/chat.html`, data: buildHtml(messages, contact) },
        { path: `${rootFolder}/messages.json`, data: JSON.stringify(json, null, 2) },
        { path: `${rootFolder}/messages.txt`, data: buildText(messages) },
        { path: `${rootFolder}/metadata.json`, data: JSON.stringify(metadata, null, 2) }
      ];
      return { contact, rootFolder, messages, mediaSaved: media.saved, itemsSkipped: media.skipped, warnings: metadata.warnings, metadata, outputFiles };
    });
  }

  async function fileBytes(outputFile) {
    return outputFile.entry ? outputFile.entry.async("uint8array") : new TextEncoder().encode(outputFile.data);
  }

  async function writeToFolder(directoryHandle, archive, onProgress = () => {}) {
    for (let i = 0; i < archive.outputFiles.length; i++) {
      const file = archive.outputFiles[i];
      onProgress("Writing archive files", `${i + 1} of ${archive.outputFiles.length}`, Math.round((i + 1) / archive.outputFiles.length * 100));
      const segments = file.path.split("/");
      let current = directoryHandle;
      for (const segment of segments.slice(0, -1)) current = await current.getDirectoryHandle(segment, { create: true });
      const handle = await current.getFileHandle(segments.at(-1), { create: true });
      const writable = await handle.createWritable();
      await writable.write(await fileBytes(file));
      await writable.close();
    }
  }

  async function createZip(archive, onProgress = () => {}) {
    const zip = new root.JSZip();
    for (const file of archive.outputFiles) zip.file(file.path, await fileBytes(file));
    return zip.generateAsync({ type: "blob", compression: "DEFLATE", compressionOptions: { level: 4 } }, metadata => {
      onProgress("Building archive ZIP", `${Math.round(metadata.percent)}%`);
    });
  }

  function parseLocalTimestamp(value) {
    const raw = clean(String(value || "")).replace(/\u202f/g, " ").replace(/\u00a0/g, " ").replace(/\b([AP])\.M\.?/ig, "$1M");
    const iso = /^(\d{4})-(\d{1,2})-(\d{1,2})[ T](\d{1,2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?(Z|[+-]\d{2}:?\d{2})?$/i.exec(raw);
    if (iso) {
      const year = Number(iso[1]), month = Number(iso[2]), day = Number(iso[3]);
      if (!validDate(year, month, day)) return null;
      return `${pad(year, 4)}-${pad(month)}-${pad(day)}T${pad(iso[4])}:${pad(iso[5])}:${pad(iso[6] || 0)}${iso[7] || ""}`;
    }
    const match = /^(\d{1,2})[/-](\d{1,2})[/-](\d{2,4}),?\s+(\d{1,2}):(\d{2})(?::(\d{2}))?\s*(AM|PM)?$/i.exec(raw);
    if (!match) return null;
    let first = Number(match[1]), second = Number(match[2]), year = Number(match[3]);
    let month, day;
    if (first > 12 && second <= 12) { day = first; month = second; }
    else if (second > 12 && first <= 12) { month = first; day = second; }
    else if (first === second && first <= 12) { month = first; day = second; }
    else return null;
    if (year < 100) year += 2000;
    if (!validDate(year, month, day)) return null;
    let hour = Number(match[4]);
    const meridiem = (match[7] || "").toUpperCase();
    if (meridiem) {
      if (hour < 1 || hour > 12) return null;
      if (meridiem === "AM" && hour === 12) hour = 0;
      if (meridiem === "PM" && hour !== 12) hour += 12;
    }
    if (hour > 23 || Number(match[5]) > 59 || Number(match[6] || 0) > 59) return null;
    return `${pad(year, 4)}-${pad(month)}-${pad(day)}T${pad(hour)}:${pad(match[5])}:${pad(match[6] || 0)}`;
  }

  function validDate(year, month, day) {
    if (month < 1 || month > 12 || day < 1 || day > 31) return false;
    const date = new Date(Date.UTC(year, month - 1, day));
    return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
  }

  function mediaCategory(value) {
    const extension = baseName(value).split(".").pop().toLowerCase();
    if (["jpg", "jpeg", "png", "gif", "webp", "heic"].includes(extension)) return "images";
    if (["mp4", "mov", "3gp", "mkv"].includes(extension)) return "videos";
    if (["opus", "m4a", "mp3", "aac", "wav", "ogg"].includes(extension)) return "audio";
    if (["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "vcf", "txt"].includes(extension)) return "documents";
    if (["zip"].includes(extension)) return "other";
    if (typeof value === "string" && /voice|audio/i.test(value)) return "audio";
    if (typeof value === "string" && /video|gif/i.test(value)) return "videos";
    if (typeof value === "string" && /photo|image/i.test(value)) return "images";
    return "other";
  }

  function uniqueMediaName(original, category, counts) {
    const safe = safeFileName(original || "attachment.bin");
    const extension = safe.includes(".") ? safe.slice(safe.lastIndexOf(".")) : "";
    const stem = extension ? safe.slice(0, -extension.length) : safe;
    const key = `${category}/${safe.toLowerCase()}`;
    const count = (counts.get(key) || 0) + 1;
    counts.set(key, count);
    return count === 1 ? safe : `${stem} (${count})${extension}`;
  }

  function safeFileName(value) {
    const base = baseName(value).replace(/[<>:"/\\|?*\u0000-\u001f]/g, "_").replace(/[ .]+$/g, "").trim();
    if (!base || base === "." || base === "..") return "attachment.bin";
    return base.length > 120 ? base.slice(-120) : base;
  }

  function baseName(value) {
    return String(value || "").replace(/\\/g, "/").split("/").pop().trim();
  }

  function clean(value) { return String(value || "").replace(/^[\s\u200e\u200f\u202a\u202c\u2066\u2069]+|[\s\u200e\u200f\u202a\u202c\u2066\u2069]+$/g, ""); }
  function pad(value, width = 2) { return String(value).padStart(width, "0"); }
  function escapeHtml(value) { return String(value ?? "").replace(/[&<>"']/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[ch]); }

  return { parseStart, parseText, parseJson, parseCsv, parseLocalTimestamp, readExport, makeArchive, writeToFolder, createZip, mediaCategory, safeFileName };
});
