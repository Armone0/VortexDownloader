// VortexDownloader Chrome / Edge Integration Service Worker
const VORTEX_BRIDGE_URL = "http://127.0.0.1:19741/download/";
const VORTEX_PING_URL = "http://127.0.0.1:19741/download/ping";

// Track fallback downloads initiated when desktop bridge is offline to prevent intercept loops
const fallbackUrls = new Set();

function sanitizeFileName(name) {
  if (!name || typeof name !== 'string') return "video.mp4";
  let clean = name.replace(/[<>:"/\\|?*\x00-\x1F]/g, "_").trim();
  if (clean.length > 80) clean = clean.substring(0, 80).trim();
  return clean || "video.mp4";
}

// Reusable function to dispatch download requests to the Vortex desktop engine
async function sendToVortex(payload) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 3000);

  try {
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
    return response.ok;
  } catch (err) {
    clearTimeout(timeout);
    return false;
  }
}

// 1. Intercept browser downloads and route to VortexDownloader via onDeterminingFilename
chrome.downloads.onDeterminingFilename.addListener((downloadItem, suggest) => {
  const url = downloadItem.finalUrl || downloadItem.url;
  
  // Ignore data/blob/extension urls
  if (!url || url.startsWith("blob:") || url.startsWith("data:") || url.startsWith("chrome-extension:")) {
    suggest();
    return;
  }

  // If this download was triggered as a fallback by the extension, do not re-intercept
  if (fallbackUrls.has(url)) {
    fallbackUrls.delete(url);
    suggest();
    return;
  }

  const payload = {
    url: url,
    fileName: downloadItem.filename ? downloadItem.filename.split(/[\\/]/).pop() : null,
    fileSize: downloadItem.fileSize > 0 ? downloadItem.fileSize : null,
    mime: downloadItem.mime || null
  };

  sendToVortex(payload).then((captured) => {
    if (captured) {
      // Vortex captured the download! Cancel browser download cleanly
      chrome.downloads.cancel(downloadItem.id, () => {
        try { chrome.downloads.erase({ id: downloadItem.id }); } catch (_) {}
      });

      chrome.action.setBadgeText({ text: "⚡" });
      chrome.action.setBadgeBackgroundColor({ color: "#10B981" });
      setTimeout(() => chrome.action.setBadgeText({ text: "" }), 3000);
    } else {
      // Safe fallback: If Vortex is offline, let Chrome download natively
      suggest();
    }
  }).catch(() => {
    suggest();
  });

  return true; // Keep async channel open for suggest()!
});

// 2. Handle media download requests from content scripts (Video Sniffer / Grabber)
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
  const captured = await sendToVortex(payload);

  if (captured) {
    chrome.action.setBadgeText({ text: "⚡" });
    chrome.action.setBadgeBackgroundColor({ color: "#10B981" });
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

// 3. Right-Click Context Menu ("⚡ Vortex ile İndir")
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: "vortex-download-context",
    title: "⚡ Vortex ile İndir",
    contexts: ["link", "video", "audio", "image"]
  });
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const targetUrl = info.linkUrl || info.srcUrl;
  if (!targetUrl || (!targetUrl.startsWith("http://") && !targetUrl.startsWith("https://"))) {
    return;
  }

  const payload = {
    url: targetUrl,
    fileName: null,
    pageUrl: tab ? tab.url : null
  };

  const captured = await sendToVortex(payload);

  if (captured) {
    chrome.action.setBadgeText({ text: "⚡" });
    chrome.action.setBadgeBackgroundColor({ color: "#10B981" });
    setTimeout(() => chrome.action.setBadgeText({ text: "" }), 3000);

    try {
      chrome.notifications.create({
        type: "basic",
        iconUrl: "icons/icon128.png",
        title: "VortexDownloader",
        message: "Bağlantı VortexDownloader'a aktarıldı!"
      });
    } catch (_) {}
  } else {
    // If Vortex is offline, fall back to browser download
    try {
      fallbackUrls.add(targetUrl);
      setTimeout(() => fallbackUrls.delete(targetUrl), 10000);

      await chrome.downloads.download({ url: targetUrl });
    } catch (_) {}
  }
});

// 4. Click Extension Icon in Toolbar: Test Connection & Display Status
chrome.action.onClicked.addListener(async (tab) => {
  try {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 1500);

    const res = await fetch(VORTEX_PING_URL, {
      method: "GET",
      signal: controller.signal
    });
    clearTimeout(timeout);

    if (res.ok) {
      chrome.action.setBadgeText({ text: "ON" });
      chrome.action.setBadgeBackgroundColor({ color: "#10B981" });
      setTimeout(() => chrome.action.setBadgeText({ text: "" }), 4000);

      chrome.notifications.create({
        type: "basic",
        iconUrl: "icons/icon128.png",
        title: "VortexDownloader Aktif ⚡",
        message: "Masaüstü motoruna bağlı (Port 19741). Dosya ve videolar otomatik yakalanıyor."
      });
      return;
    }
  } catch (_) {}

  // Offline notification
  chrome.action.setBadgeText({ text: "OFF" });
  chrome.action.setBadgeBackgroundColor({ color: "#EF4444" });
  setTimeout(() => chrome.action.setBadgeText({ text: "" }), 4000);

  chrome.notifications.create({
    type: "basic",
    iconUrl: "icons/icon128.png",
    title: "VortexDownloader Çevrimdışı ⚠️",
    message: "Masaüstü uygulaması kapalı görünüyor. Lütfen VortexDownloader'ı başlatın."
  });
});
