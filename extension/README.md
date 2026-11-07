# VortexDownloader Browser Extension ⚡ (v1.2.1)

Companion Manifest V3 browser extension for Google Chrome, Microsoft Edge, and Chromium-based browsers.

## 🚀 Features
- **Floating Video & Media Grabber (Video Sniffer):**
  - Automatically detects HTML5 `<video>`, `<source>` streams, and dynamically loaded media players across websites.
  - Injects a sleek, modern glassmorphism floating pill button directly attached to the video player: `⚡ Vortex: Videoyu İndir`.
  - One-click instant dispatch to the VortexDownloader desktop engine with optimal filename detection and desktop prompt.
  - Non-intrusive UI with smooth hover animations and a dedicated close button (`×`) to dismiss the pill per video if desired.
- **Seamless File Download Capture:**
  - Automatically intercepts standard browser file downloads and hands them over to the multi-connection VortexDownloader engine (`http://127.0.0.1:19741/download/`).
- **Zero-Drop Graceful Fallback:**
  - If the VortexDownloader desktop app is offline or unreachable, downloads and media requests seamlessly resume or start directly via the browser's native download manager with a helpful system notification.
- **Privacy & Performance:**
  - 100% local loopback communication (`127.0.0.1:19741`), zero external network requests, zero analytics, zero telemetry.

## 📦 Installation (Developer Mode)
1. Open your browser and navigate to:
   - Chrome: `chrome://extensions`
   - Edge: `edge://extensions`
2. Enable **Developer Mode** (Geliştirici Modu - top-right toggle).
3. Click **Load unpacked** (Paketlenmemiş öğe yükle).
4. Select this `extension` folder (or `VortexExtension`).
5. Ensure VortexDownloader desktop app is running. Any video or file download initiated in the browser will now trigger the instant Vortex download prompt!
