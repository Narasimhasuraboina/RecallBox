# RecallBox

RecallBox is a free, local Windows app for saving WhatsApp chat exports as readable archives. It stores the archive on your computer and can show its progress in a browser on Windows or Android. Messages and media are not uploaded to a cloud service.

## Quick start on Windows

1. Install the free [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows 10 or 11.
2. Download this repository from GitHub and extract it, or clone it:

   ```powershell
   git clone https://github.com/Narasimhasuraboina/RecallBox.git
   cd RecallBox
   ```

3. Double-click **Start-RecallBox-Background.vbs**. RecallBox starts quietly in the background; it does not open WhatsApp.
4. Open <http://localhost:5180> in your browser.
5. On your phone, open the WhatsApp conversation, choose **Export chat**, and include media if WhatsApp offers that option. Transfer the resulting `.zip` to the Windows computer.
6. In RecallBox, choose the archive folder, enter the contact number with its country code, select the export `.zip`, confirm it belongs to your account, then choose **Import export file**.
7. Find the archive in the selected folder under `Contact-<phone digits>`. Open `chat.html` to read it.

The export importer works even when WhatsApp Desktop is closed or not installed on the Windows computer. To see startup errors, run `Start-RecallBox.ps1` in PowerShell instead of the hidden launcher. Stop the background app by running `Stop-RecallBox.ps1` in PowerShell.

## What the Windows chat scan can do

If WhatsApp Desktop is already running and you have selected the target chat yourself, **Download Chat** can read the message controls exposed in that view or use local OCR on the visible window. RecallBox does not launch, focus, search, click, or scroll WhatsApp. This scan captures only the current view; it cannot retrieve older messages while running in the background. A displayed phone number may not be available for independent verification, so confirm the selected chat in the app before starting.

Use the phone's **Export chat** feature for a fuller transcript and any media included in the export. Linked devices can have incomplete history, and WhatsApp exports may also omit content. RecallBox reports known gaps and never labels an archive historically complete without evidence.

## Android access

The default service is localhost-only. To let an Android device on your private network view progress or upload an export, run this in PowerShell:

```powershell
.\Start-RecallBox.ps1 -Lan
```

Use the private URL and one-run access token printed in the Windows console on your Android browser. Keep both devices on the same private network. The Windows computer and RecallBox must remain on. Stop the app with `Stop-RecallBox.ps1`; the next LAN launch creates a new token. Do not share the token outside your network.

## Archive contents

Each chat is stored in `Contact-<phone digits>` with:

- `chat.html` — responsive conversation view
- `messages.json` — structured message records
- `messages.txt` — plain text transcript
- `metadata.json` — status, counts, warnings, and known limitations
- `media/images`, `media/videos`, `media/audio`, `media/documents`, `media/other`
- `.recallbox/messages.jsonl` — resumable local checkpoint

RecallBox remembers only the preferred destination in `%LOCALAPPDATA%\RecallBox\settings.json`. Archives and indexes stay in the destination you choose.

## Privacy and limits

- No paid services, API subscriptions, or cloud hosting are required.
- The service binds to localhost by default. LAN access is opt-in and API requests require the one-run token.
- RecallBox does not inspect WhatsApp databases, decrypt account data, request credentials, or save OCR screenshots.
- The direct Windows scan can read only the currently visible chat view and cannot retrieve media binaries. Import a phone export ZIP for media files included in that export.
- `metadata.json` reports missing items and uncertainty. A successful import means the supplied export was parsed; it does not prove that WhatsApp supplied complete account history.

WhatsApp describes chat export in its [official help article](https://faq.whatsapp.com/1180414079177245/) and notes that exports are not restorable backups.

## Build from source

On Windows with the .NET 8 SDK:

```powershell
dotnet build .\RecallBox.App\RecallBox.App.csproj -c Release
```

`RecallBox.Poc` contains optional Windows UI Automation diagnostics. Run it only if you want to inspect your own WhatsApp client; the main RecallBox app does not interact with the WhatsApp window.
