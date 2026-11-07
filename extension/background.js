// VortexDownloader Chrome / Edge Integration Service Worker
const VORTEX_BRIDGE_URL = "http://127.0.0.1:19741/download/";

// Track fallback downloads initiated when desktop bridge is offline to prevent intercept loops
const fallbackUrls = new Set();

function sanitizeFileName(name) {
  if (!name || typeof name !== 'string') return "video.mp4";
  let clean = name.replace(/[<>:"/\\|?*\x00-\x1F]/g, "_").trim();
  if (clean.length > 80) clean = clean.substring(0, 80).trim();
  return clean || "video.mp4";
}

// Intercept browser downloads and route to VortexDownloader
chrome.downloads.onCreated.addListener(async (downloadItem) => {
  const url = downloadItem.finalUrl || downloadItem.url;
  
  // Ignore data/blob/extension urls
  if (!url || url.startsWith("blob:") || url.startsWith("data:") || url.startsWith("chrome-extension:")) {
    return;
  }

  // If this download was triggered as a fallback by the extension, do not re-intercept
  if (fallbackUrls.has(url)) {
    fallbackUrls.delete(url);
    return;
  }

  // Immediately pause browser download to negotiate with Vortex
  try {
    await chrome.downloads.pause(downloadItem.id);
  } catch (e) {
    // Ignore if already completed/paused
  }

  const payload = {
    url: url,
    fileName: downloadItem.filename ? downloadItem.filename.split(/[\\/]/).pop() : null,
    fileSize: downloadItem.fileSize > 0 ? downloadItem.fileSize : null,
    mime: downloadItem.mime || null
  };

  try {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 1200);

    const response = await fetch(VORTEX_BRIDGE_URL, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Vortex-Auth": "vortex-bridge-extension-auth-token"
      },
      body: JSON.stringify(payload),
      signal: controller.signal
    });

    clearTimeout(timeout);

    if (response.ok) {
      // Vortex captured the download! Cancel browser download cleanly
      await chrome.downloads.cancel(downloadItem.id);
      await chrome.downloads.erase({ id: downloadItem.id });
      
      chrome.action.setBadgeText({ text: "⚡" });
      chrome.action.setBadgeBackgroundColor({ color: "#00F2FE" });
      setTimeout(() => chrome.action.setBadgeText({ text: "" }), 3000);
      return;
    }
  } catch (err) {
    // VortexDownloader is not running or rejected; safely resume in browser!
  }

  // Safe fallback: Resume in Chrome/Edge without breaking user download
  try {
    await chrome.downloads.resume(downloadItem.id);
  } catch (e) {
    // Already in a valid state
  }
});

// Handle media download requests from content scripts (Video Sniffer / Grabber)
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message && message.action === "download_media") {
    handleMediaDownload(message, sender).then(sendResponse);
    return true; // Keep message channel open for async response
  }
});

async function handleMediaDownload(message, sender) {
  const url = message.url;
  if (!url || (!url.startsWith("http://") && !url.startsWith("https://"))) {
    return { success: false, error: "Geçersiz video bağlantısı" };
  }

  const suggestedFileName = sanitizeFileName(message.suggestedFileName || message.pageTitle || "vortex_video.mp4");
  const pageUrl = message.pageUrl || (sender && sender.tab ? sender.tab.url : null);

  const payload = {
    url: url,
    fileName: suggestedFileName,
    suggestedFileName: suggestedFileName,
    pageUrl: pageUrl
  };

  // 1. Attempt transfer to local VortexDownloader desktop bridge
  try {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 1500);

    const response = await fetch(VORTEX_BRIDGE_URL, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Vortex-Auth": "vortex-bridge-extension-auth-token"
      },
      body: JSON.stringify(payload),
      signal: controller.signal
    });

    clearTimeout(timeout);

    if (response.ok) {
      chrome.action.setBadgeText({ text: "⚡" });
      chrome.action.setBadgeBackgroundColor({ color: "#00F2FE" });
      setTimeout(() => chrome.action.setBadgeText({ text: "" }), 3000);

      try {
        chrome.notifications.create({
          type: "basic",
          iconUrl: "icons/icon128.png",
          title: "VortexDownloader",
          message: `Video Vortex'e aktarıldı: ${suggestedFileName}`
        });
      } catch (_) {}

      return { success: true, bridge: true };
    }
  } catch (err) {
    // Vortex bridge is not reachable (desktop app is closed)
  }

  // 2. Graceful Fallback: Initiate native browser download
  try {
    fallbackUrls.add(url);
    setTimeout(() => fallbackUrls.delete(url), 10000);

    await chrome.downloads.download({
      url: url,
      filename: suggestedFileName
    });

    try {
      chrome.notifications.create({
        type: "basic",
        iconUrl: "icons/icon128.png",
        title: "VortexDownloader (Tarayıcı İndirmesi)",
        message: "Masaüstü uygulaması çevrimdışı. İndirme tarayıcı üzerinden devam ediyor."
      });
    } catch (_) {}

    return { success: true, bridge: false, fallback: true };
  } catch (downloadErr) {
    return { success: false, error: downloadErr.message || "İndirme başlatılamadı" };
  }
}
