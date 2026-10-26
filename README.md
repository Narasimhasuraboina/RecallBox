# RecallBox

RecallBox is a browser-only chat export converter. Select a WhatsApp export—or a ZIP containing a supported chat transcript—and RecallBox creates a readable chat page, structured JSON, a text transcript, metadata, and folders for the media included in the export.

**No .NET, server, account, or WhatsApp desktop app is needed.** The selected file is processed in your browser; RecallBox does not upload chat content.

## Use it

1. On your phone, open the WhatsApp conversation and choose **Export chat**. Include media if WhatsApp offers that option.
2. Transfer the `.zip` or `.txt` export to the device where you will use RecallBox.
3. Open `index.html` in a modern browser, or open the hosted static site over HTTPS.
4. Enter the contact number with its country code, choose the export, and confirm it belongs to your account.
5. Choose **Choose folder** if your browser supports direct folder access. RecallBox creates a new `Contact-<digits>` folder there. If the folder picker is unavailable, RecallBox downloads one ZIP containing the same folder structure; save and extract it where you want.
6. Open `Contact-<digits>/chat.html` to read the archive. `messages.json` contains structured records.

To use the repository locally, download and extract it or clone it:

```sh
git clone https://github.com/Narasimhasuraboina/RecallBox.git
cd RecallBox
```

Then open `index.html`. The ZIP library is included locally, so conversion works without a package install or external script. An HTTPS static host can enable directory selection in browsers that require a secure context; hosting the page does not send the selected files to the host.

RecallBox follows your device's light/dark setting on first open. Use the theme button in the header to switch themes; your choice is remembered in that browser.

## Supported exports

- WhatsApp `.txt` exports and `.zip` exports containing the transcript and media.
- ZIP files containing a timestamped `.txt` transcript, a JSON array or object with a `messages`/`items` list, or a CSV with timestamp, sender, and message columns.
- Standalone `.txt`, `.json`, and `.csv` files using those same transcript formats.

ZIPs are inspected locally. When several transcript files are present, RecallBox chooses the supported transcript with the most recognized messages and reports that choice. Attachments referenced by filename are matched to ZIP entries; duplicate filenames are handled separately. Missing or omitted files are marked in the original message and in `metadata.json`. Arbitrary binary ZIPs without a recognized transcript are rejected instead of guessed.

## Output structure

```text
Contact-<digits>/
├── chat.html
├── messages.json
├── messages.txt
├── metadata.json
└── media/
    ├── images/
    ├── videos/
    ├── audio/
    ├── documents/
    └── other/
```

The JSON preserves source order, sender, original timestamp text, parsed dates when unambiguous, message text, and attachment links. Metadata records counts, missing media, warnings, and the selected source file. Existing contact folders are not overwritten when saving directly; RecallBox chooses a new numbered folder.

## Local file handling

- RecallBox reads the selected export in the browser and makes no upload or analytics requests.
- Direct folder saving uses the browser's File System Access API and asks you to choose a folder. Browser support varies, and a secure context/user gesture may be required. If unavailable, use the archive ZIP download.
- On Android and browsers without folder writing, the ZIP download is the portable option; extract it with the device's file manager.
- Input files are limited to 512 MB, transcripts to 128 MB, ZIP entry count to 20,000, and expanded ZIP contents to 2 GB to limit browser memory use.
- Exports can omit messages or media. Ambiguous dates remain as text and are not guessed. RecallBox never claims that an export contains complete account history.

See the browser's [directory picker documentation](https://developer.mozilla.org/en-US/docs/Web/API/Window/showDirectoryPicker) for current compatibility details.

## Project files

- `index.html`, `styles.css`, `app.js` — static interface and local save/download flow
- `converter.js` — transcript parsing, archive generation, metadata, and media matching
- `vendor/jszip.min.js` — vendored JSZip 3.10.1 for local ZIP reading/writing
- `vendor/JSZip-LICENSE.txt` — JSZip MIT license

There is no build step or server process. To update the app, edit these files and reload the page.
