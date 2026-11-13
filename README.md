<div align="center">

<img src="src/VortexDownloader.UI/Assets/vortex_logo.png" alt="VortexDownloader logo" width="96"/>

# VortexDownloader

**A fast, open-source download manager for Windows.**
Segmented downloads, browser capture, and file picking inside remote ZIP/RAR archives, with no ads or telemetry.

[![Latest release](https://img.shields.io/github/v/release/Armone0/VortexDownloader?label=release&color=8EA191)](https://github.com/Armone0/VortexDownloader/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Armone0/VortexDownloader/total?color=8EA191)](https://github.com/Armone0/VortexDownloader/releases)
[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows)](#-quick-start)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/9.0)
[![Tests](https://img.shields.io/badge/tests-177%20passing-2EA043)](tests/)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](LICENSE)

### [⬇️ Download for Windows](https://github.com/Armone0/VortexDownloader/releases/latest)

<sub>Portable · single .exe · no installer · no .NET runtime needed</sub>

<br/>

<img src="docs/screenshots/main-window.png" alt="VortexDownloader main window: segmented downloads with a live chunk map, multi-mirror source chip and Defender scan badge" width="860"/>

[Features](#-features) · [Quick start](#-quick-start) · [Browser extension](#-browser-extension) · [Security](#-security--privacy) · [Build](#-build-from-source) · [FAQ](#-faq) · [Türkçe](#-türkçe)

</div>

---

## ✨ Highlights

- **Up to 32 connections per file.** When a connection finishes early, the largest remaining piece is split in half so idle connections help finish the tail.
- **Take only what you need from an archive.** Open a remote `.zip` or `.rar`, tick the files you want, and only those bytes are downloaded through HTTP Range requests.
- **Works with your browser.** The Chrome/Edge/Brave extension sends downloads to Vortex together with the page's cookies, so files behind a login (such as Moodle) work too.
- **Checks every file.** Finished downloads get a SHA-256 hash and a Windows Defender scan, and the card shows a "Clean" or "Threat found" badge.

## 🚀 Features

| | |
| :--- | :--- |
| ⚡ **Segmented engine** | 1–32 connections, dynamic chunk splitting, pooled buffers (`ArrayPool<byte>`), and HTTP/1.1 and HTTP/2 via `SocketsHttpHandler`. |
| ⇄ **Multi-mirror** | Add mirror URLs for the same file. Chunks are spread across every valid mirror so speeds add up, and if one drops its chunks move to the others. |
| 🗜️ **ZIP & RAR file picking** | Lists an archive's contents without downloading it. Picked files are fetched by Range, CRC-32 checked, and never overwrite existing files. RAR5 (RAR4 via the library, untested) and solid archives are supported. |
| 🔁 **Expired-link resume** | When a signed link expires, the downloaded data is kept. Download the file again from the page and Vortex continues where it stopped, checking the size and ETag/Last-Modified first. |
| 🎮 **Game mode** | While a fullscreen game, film or presentation runs, downloads slow to 256 KB/s – 1 MB/s. Your own limit comes back afterwards. |
| 🛡️ **Integrity & safety** | SHA-256 hash, Windows Defender scan, resume integrity checks (ETag, Last-Modified, length), and a disk-space check before each download. |
| 🌐 **Browser capture** | A Manifest V3 extension catches downloads and sends cookies, referrer and user agent. A one-time pairing in the app is required. A floating button grabs page videos. |
| 🎬 **Media** | Video and audio downloads through `yt-dlp` and `ffmpeg` (for example YouTube). Both are downloaded automatically the first time you use media. |
| 📋 **Queue & batch** | Paste many URLs at once. Set how many downloads run at once (1–5 or unlimited), pause or resume them all, and filter by category. |
| 🎯 **Speed limiter** | Token-bucket limit (1 / 5 / 10 MB/s or unlimited), applied across all downloads. |
| 💾 **Crash-safe state** | Progress is saved atomically to `%APPDATA%\VortexDownloader`, so interrupted downloads resume without corruption. |
| 🎨 **Polished UI** | Dark and light themes, English and Turkish, tray icon, Windows 11 taskbar progress, and options to stop sleep or shut down when done. |

## 📦 Quick start

1. Download **`VortexDownloader-vX.Y.Z-win-x64.zip`** from the [latest release](https://github.com/Armone0/VortexDownloader/releases/latest).
2. Extract it anywhere and run **`VortexDownloader.exe`**.
3. *(Optional)* Install the browser extension (see below) so downloads from your browser go to Vortex.

> [!NOTE]
> The binary is not code-signed, so Windows SmartScreen may say *"Windows protected your PC"* on first run. Click **More info → Run anyway**. Every release has a `.sha256` file you can use to verify the download.

## 🧩 Browser extension

The extension lives in the [`extension/`](extension/) folder of the repository and is included in the release ZIP.

1. Open `chrome://extensions` (Edge: `edge://extensions`, Brave: `brave://extensions`).
2. Turn on **Developer mode**.
3. Click **Load unpacked** and select the `extension` folder.
4. Keep `VortexDownloader.exe` running. On first use the app asks you to approve the extension (one-time pairing).

If the app is closed, the browser downloads the file normally. Nothing is lost.

## 🔒 Security & privacy

- **No telemetry, no accounts, no ads.** Vortex only connects to the servers you download from. The one exception: the first time you use media downloads, it fetches `yt-dlp` (from GitHub) and `ffmpeg` (from gyan.dev).
- **Local bridge hardening.** The bridge listens only on `127.0.0.1:19741`. It checks the `Host` header (to block DNS rebinding), blocks cross-site `Sec-Fetch-Site` requests, and allows only extension origins.
- **Per-install pairing.** Each install generates a random 256-bit token, and only extensions you approved in the app can queue downloads. Tokens are compared in constant time.
- **Safe file writes.** File names are sanitized, archive paths can't escape the target folder (zip-slip protection), existing files are never overwritten, and decompressed output has a size cap.
- **Cookies stay with their site.** In multi-mirror downloads, the main site's cookies and referrer are never sent to other mirror hosts.

Found a vulnerability? Please open a private security advisory on GitHub instead of a public issue.

<details>
<summary><b>🛠️ Architecture</b></summary>

```mermaid
flowchart LR
    subgraph Browser["Browser (Chrome / Edge / Brave)"]
        EXT["Extension<br/>(MV3 service worker)"]
    end
    subgraph App["VortexDownloader (.NET 9 / WPF)"]
        BRIDGE["Loopback bridge :19741<br/>pairing token · host/origin checks"]
        PROMPT["Download prompt<br/>(ZIP/RAR picker)"]
        ENGINE["DownloadEngine<br/>chunks · mirrors · resume checks"]
        LIMIT["Speed limiter<br/>(+ game mode)"]
        WORKERS["Chunk workers<br/>(Range requests)"]
        STATE["StateStore<br/>(%APPDATA%)"]
        CHECK["SHA-256 + Defender"]
    end
    SERVERS["Web servers / mirrors"]

    EXT -->|paired POST| BRIDGE --> PROMPT --> ENGINE --> LIMIT --> WORKERS
    WORKERS <-->|parallel Range| SERVERS
    ENGINE --> STATE
    WORKERS --> CHECK
```

| Project | What it holds |
| :--- | :--- |
| `src/VortexDownloader.Core` | Engine, chunk scheduling, ZIP/RAR readers, speed limiter, state store (no UI) |
| `src/VortexDownloader.UI` | WPF app, local bridge, dialogs, themes, localization |
| `extension/` | Chrome MV3 extension |
| `tests/VortexDownloader.Tests` | xUnit tests (engine, mirrors, archives, security, extension) |

</details>

## 🏗️ Build from source

Requirements: Windows 10 (19041+) or 11, [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
git clone https://github.com/Armone0/VortexDownloader.git
cd VortexDownloader
dotnet test
dotnet run --project src/VortexDownloader.UI -c Release
```

Single-file portable build:

```bash
dotnet publish src/VortexDownloader.UI -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## 🗺️ Roadmap

- [x] ZIP & RAR file picking, multi-mirror, game mode (v1.8)
- [x] Expired-link resume, Defender + SHA-256, extension pairing (v1.7)
- [ ] Send a link from your phone to the PC with a QR code (LAN)
- [ ] Long-running memory soak test in CI
- [ ] Chrome Web Store listing

See the full history in [CHANGELOG.md](CHANGELOG.md).

## ❓ FAQ

<details>
<summary><b>Is this related to Internet Download Manager (IDM)?</b></summary>

No. VortexDownloader is an independent clean-room project with no code, binaries or assets from any commercial product. "Internet Download Manager" and "IDM" are trademarks of Tonec Inc. See [DISCLAIMER.md](DISCLAIMER.md).
</details>

<details>
<summary><b>Why is my download not faster with more connections?</b></summary>

Some servers limit connections per user or do not support Range requests. In that case the card shows "Range: No" and the file downloads with one connection. For most servers, 8 connections is enough.
</details>

<details>
<summary><b>Can I pick files from a password-protected RAR?</b></summary>

Not yet. Encrypted entries are listed but can't be picked. Download the whole archive and extract it with your archiver.
</details>

<details>
<summary><b>Does it run on macOS or Linux?</b></summary>

No. The UI is WPF and Windows-only.
</details>

## 🤝 Contributing

Bug reports, ideas and pull requests are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) and make sure `dotnet test` passes before opening a PR.

## 🇹🇷 Türkçe

**VortexDownloader**, Windows için açık kaynaklı ve hızlı bir indirme yöneticisidir.
- Dosyaları 32 bağlantıya kadar parçalayarak indirir ve birden fazla aynadan aynı anda çekebilir.
- ZIP ve RAR arşivlerinin içinden yalnızca istediğin dosyaları indirir.
- Oyun açıkken indirmeyi kendiliğinden yavaşlatır.
- İnen her dosyayı Windows Defender ile tarar.

Arayüz Türkçe ve İngilizce kullanılabilir. [Son sürümü indir](https://github.com/Armone0/VortexDownloader/releases/latest) · [Değişiklik günlüğü](CHANGELOG.md)

## 📄 License

[Apache License 2.0](LICENSE). Third-party: [SharpCompress](https://github.com/adamhathcock/sharpcompress) (MIT) for RAR reading and [yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense) for media.
