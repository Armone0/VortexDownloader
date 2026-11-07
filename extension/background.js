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

// State cache & sync for download interception toggle
function syncInterceptionBadge(enabled) {
  if (!chrome.action || !chrome.action.setBadgeText) return;
  if (enabled) {
    chrome.action.setBadgeText({ text: "" });
  } else {
    chrome.action.setBadgeText({ text: "OFF" });
    if (chrome.action.setBadgeBackgroundColor) {
      chrome.action.setBadgeBackgroundColor({ color: "#6B7280" });
    }
  }
}

// Persistent diagnostic event logger (retains last 50 events in chrome.storage.local)
function addLog(level, message, details = null) {
  const time = new Date().toLocaleTimeString();
  const entry = { time, level, message, details: details ? (typeof details === 'object' ? JSON.stringify(details) : String(details)) : null };
  console.log(`[Vortex ${level.toUpperCase()} ${time}]`, message, details || "");

  try {
    if (typeof chrome !== 'undefined' && chrome.storage && chrome.storage.local) {
      chrome.storage.local.get({ debugLogs: [] }, (res) => {
        const logs = res ? (res.debugLogs || []) : [];
        logs.push(entry);
        if (logs.length > 50) logs.shift();
        chrome.storage.local.set({ debugLogs: logs });
      });
    }
  } catch (_) {}
}

function showDesktopNotification(title, message) {
  try {
    if (typeof chrome !== 'undefined' && chrome.notifications && chrome.notifications.create) {
      chrome.notifications.create({
        type: "basic",
        iconUrl: "icons/icon128.png",
        title: title || "VortexDownloader",
        message: message || ""
      });
    }
  } catch (_) {}
}

// Initialize badge on startup
if (chrome.storage && chrome.storage.local) {
  chrome.storage.local.get({ interceptEnabled: true }, (items) => {
    const isEnabled = items ? items.interceptEnabled !== false : true;
    syncInterceptionBadge(isEnabled);
  });

  chrome.storage.onChanged.addListener((changes, area) => {
    if (area === "local" && changes.interceptEnabled) {
      syncInterceptionBadge(changes.interceptEnabled.newValue !== false);
    }
  });
}

// Safely parse URL into protocol and hostname across browser service worker and test VM sandboxes
function parseUrl(urlStr) {
  if (!urlStr || typeof urlStr !== 'string') return null;
  if (typeof URL !== 'undefined') {
    try {
      const u = new URL(urlStr);
      if (!u.hostname) return null;
      return { protocol: u.protocol.toLowerCase(), hostname: u.hostname.toLowerCase() };
    } catch (_) {
      return null;
    }
  }
  try {
    const a = urlStr.indexOf('://');
    if (a === -1) return null;
    const protocol = urlStr.substring(0, a + 1).toLowerCase();
    let rest = urlStr.substring(a + 3);
    const slashIdx = rest.indexOf('/');
    const qIdx = rest.indexOf('?');
    const hIdx = rest.indexOf('#');
    let endHost = rest.length;
    if (slashIdx !== -1 && slashIdx < endHost) endHost = slashIdx;
    if (qIdx !== -1 && qIdx < endHost) endHost = qIdx;
    if (hIdx !== -1 && hIdx < endHost) endHost = hIdx;
    let hostPort = rest.substring(0, endHost);
    const atIdx = hostPort.lastIndexOf('@');
    if (atIdx !== -1) hostPort = hostPort.substring(atIdx + 1);
    let hostname = hostPort;
    if (hostPort.startsWith('[') && hostPort.includes(']')) {
      hostname = hostPort.substring(0, hostPort.indexOf(']') + 1);
    } else {
      const colonIdx = hostPort.indexOf(':');
      if (colonIdx !== -1) hostname = hostPort.substring(0, colonIdx);
    }
    hostname = hostname.trim().toLowerCase();
    if (!hostname) return null;
    return { protocol, hostname };
  } catch (_) {
    return null;
  }
}

// Extract registrable domain (eTLD+1) supporting ccSLDs (.co.uk, .com.tr, etc.), IPs and localhost
function getRootDomain(hostname) {
  if (!hostname || typeof hostname !== 'string') return '';
  const host = hostname.trim().toLowerCase();

  // IPv4, IPv6, or single-label hostname (e.g. localhost)
  if (/^(\d{1,3}\.){3}\d{1,3}$/.test(host) || host.includes(':') || !host.includes('.')) {
    return host;
  }

  const parts = host.split('.');
  if (parts.length <= 2) {
    return host;
  }

  // Common second-level domains under country-code TLDs (.co.uk, .com.tr, .org.au, etc.)
  const commonSecondLevelTLDs = new Set([
    'co', 'com', 'org', 'net', 'edu', 'gov', 'ac', 'mil', 'nom', 'gen'
  ]);

  const last = parts[parts.length - 1];
  const secondLast = parts[parts.length - 2];

  if (last.length === 2 && commonSecondLevelTLDs.has(secondLast) && parts.length >= 3) {
    return parts.slice(-3).join('.');
  }

  return parts.slice(-2).join('.');
}

// Enforce same security domain (eTLD+1) boundary and prevent HTTPS->HTTP credential downgrade
function isSameSecurityDomain(sourceUrl, targetUrl) {
  if (!sourceUrl || !targetUrl) return false;
  const src = parseUrl(sourceUrl);
  const tgt = parseUrl(targetUrl);

  if (!src || !tgt || !src.hostname || !tgt.hostname) {
    return false;
  }

  if (!['http:', 'https:'].includes(src.protocol) || !['http:', 'https:'].includes(tgt.protocol)) {
    return false;
  }

  if (src.protocol === 'https:' && tgt.protocol === 'http:') {
    return false;
  }

  if (src.hostname === tgt.hostname) {
    return true;
  }

  const srcRoot = getRootDomain(src.hostname);
  const tgtRoot = getRootDomain(tgt.hostname);

  return Boolean(srcRoot && tgtRoot && srcRoot === tgtRoot);
}

// Extract cookies for authenticated downloads (Google Drive, GitHub, Mediafire, etc.)
async function getCookiesForUrl(url) {
  if (!url || typeof url !== 'string' || (!url.startsWith('http://') && !url.startsWith('https://'))) {
    return null;
  }
  try {
    if (!chrome.cookies || !chrome.cookies.getAll) return null;
    const cookies = await chrome.cookies.getAll({ url: url });
    if (cookies && cookies.length > 0) {
      return cookies.map(c => `${c.name}=${c.value}`).join("; ");
    }
  } catch (err) {
    console.warn('[Vortex] Failed to extract cookies for:', url, err);
  }
  return null;
}

// Reusable function to dispatch download requests to the Vortex desktop engine
async function sendToVortex(payload) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 3000);

  try {
    addLog("info", `Masaüstü köprüsüne istek gönderiliyor: "${payload.fileName || 'isimsiz'}"`, payload.url);

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
      addLog("success", `İndirme başarıyla Vortex'e aktarıldı! (HTTP ${response.status})`);
      return true;
    } else {
      const errText = await response.text().catch(() => "");
      const msg = `Masaüstü HTTP ${response.status} hatası döndü: ${errText || response.statusText}`;
      addLog("error", msg);
      showDesktopNotification("VortexDownloader Hatası", msg);
      return false;
    }
  } catch (err) {
    clearTimeout(timeout);
    let msg = "Masaüstü uygulamasına bağlanılamadı (Port 19741 kapalı).";
    if (err && err.name === "AbortError") {
      msg = "Masaüstü uygulamasına bağlantı zaman aşımına uğradı (3s).";
    } else if (err && err.message) {
      msg = `Bağlantı hatası: ${err.message}`;
    }
    addLog("error", msg);
    showDesktopNotification("VortexDownloader Uyarısı", `${msg}\nLütfen VortexDownloader.exe'yi çalıştırın.`);
    return false;
  }
}

// 1. Intercept browser downloads and route to VortexDownloader via onDeterminingFilename
chrome.downloads.onDeterminingFilename.addListener((downloadItem, suggest) => {
  const url = downloadItem.finalUrl || downloadItem.url;
  const fileName = downloadItem.filename ? downloadItem.filename.split(/[\\/]/).pop() : "isimsiz";

  addLog("info", `İndirme algılandı: #${downloadItem.id} "${fileName}"`, { url, mime: downloadItem.mime });

  // Ignore data/blob/extension urls
  if (!url || url.startsWith("blob:") || url.startsWith("data:") || url.startsWith("chrome-extension:")) {
    addLog("warn", `Desteklenmeyen protokol (atlanıyor): ${url}`);
    suggest();
    return;
  }

  // If this download was triggered as a fallback by the extension, do not re-intercept
  if (fallbackUrls.has(url)) {
    addLog("info", `Yedek indirme listesinde, tarayıcıya devredildi: ${url}`);
    fallbackUrls.delete(url);
    suggest();
    return;
  }

  // Check if download interception is enabled in user settings
  const checkStorage = (callback) => {
    if (typeof chrome !== 'undefined' && chrome.storage && chrome.storage.local) {
      chrome.storage.local.get({ interceptEnabled: true }, (items) => {
        callback(items ? items.interceptEnabled !== false : true);
      });
    } else {
      callback(true);
    }
  };

  checkStorage(async (isEnabled) => {
    if (!isEnabled) {
      addLog("warn", "Kullanıcı ayarlarında otomatik yakalama KAPALI. Tarayıcıya devredildi.");
      suggest();
      return;
    }

    let cookieString = null;
    try {
      const destinationUrl = downloadItem.finalUrl || downloadItem.url;
      cookieString = await getCookiesForUrl(destinationUrl);

      // Enforce same security domain (eTLD+1) boundary before inheriting cookies from initial URL.
      // This strictly prevents leaking sensitive session cookies to third-party CDNs or untrusted redirect targets.
      if (!cookieString && downloadItem.finalUrl && downloadItem.url && downloadItem.finalUrl !== downloadItem.url) {
        if (isSameSecurityDomain(downloadItem.url, downloadItem.finalUrl)) {
          cookieString = await getCookiesForUrl(downloadItem.url);
        }
      }
    } catch (_) {}

    const referer = downloadItem.referrer || "";
    const userAgent = (typeof navigator !== 'undefined' && navigator.userAgent) ? navigator.userAgent : "";

    const payload = {
      url: url,
      fileName: fileName,
      fileSize: downloadItem.fileSize > 0 ? downloadItem.fileSize : null,
      mime: downloadItem.mime || null,
      cookies: cookieString,
      referer: referer,
      referrer: referer,
      userAgent: userAgent
    };

    try {
      const captured = await sendToVortex(payload);
      if (captured) {
        // ALWAYS call suggest() first so Chromium's filename determination promise resolves cleanly!
        suggest();
        chrome.downloads.cancel(downloadItem.id, () => {
          if (chrome.runtime && chrome.runtime.lastError) {
            // Suppress error if download was already cancelled, interrupted, or finished
          }
          try {
            chrome.downloads.erase({ id: downloadItem.id }, () => {
              if (chrome.runtime && chrome.runtime.lastError) {
                // Suppress error if download item was already removed
              }
            });
          } catch (_) {}
        });

        if (typeof chrome !== 'undefined' && chrome.action) {
          chrome.action.setBadgeText({ text: "⚡" });
          if (chrome.action.setBadgeBackgroundColor) {
            chrome.action.setBadgeBackgroundColor({ color: "#10B981" });
          }
          setTimeout(() => {
            if (chrome.storage && chrome.storage.local) {
              chrome.storage.local.get({ interceptEnabled: true }, (res) => {
                syncInterceptionBadge(res ? res.interceptEnabled !== false : true);
              });
            } else {
              chrome.action.setBadgeText({ text: "" });
            }
          }, 3000);
        }
      } else {
        // Safe fallback: If Vortex is offline, let Chrome download natively
        addLog("warn", `İndirme Vortex'e aktarılamadı, tarayıcıda devam ediyor: "${fileName}"`);
        suggest();
      }
    } catch (err) {
      addLog("error", "onDeterminingFilename içinde hata, tarayıcıya bırakıldı:", err ? err.message : String(err));
      suggest();
    }
  });

  return true; // Keep async channel open for suggest()!
});

// 2. Handle media download requests, link interceptions, and diagnostic log requests
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message && message.action === "download_media") {
    handleMediaDownload(message, sender).then(sendResponse);
    return true; // Keep message channel open for async response
  }
  if (message && message.action === "intercept_link") {
    handleInterceptLink(message, sender).then(sendResponse);
    return true; // Keep message channel open for async response
  }
  if (message && message.action === "get_logs") {
    if (typeof chrome !== 'undefined' && chrome.storage && chrome.storage.local) {
      chrome.storage.local.get({ debugLogs: [] }, (res) => {
        sendResponse({ logs: res ? (res.debugLogs || []) : [] });
      });
      return true;
    }
    sendResponse({ logs: [] });
    return false;
  }
  if (message && message.action === "clear_logs") {
    if (typeof chrome !== 'undefined' && chrome.storage && chrome.storage.local) {
      chrome.storage.local.set({ debugLogs: [] }, () => {
        sendResponse({ success: true });
      });
      return true;
    }
    sendResponse({ success: false });
    return false;
  }
});

async function handleMediaDownload(message, sender) {
  const url = message.url;
  if (!url || (!url.startsWith("http://") && !url.startsWith("https://"))) {
    return { success: false, error: "Geçersiz video bağlantısı" };
  }

  const suggestedFileName = sanitizeFileName(message.suggestedFileName || message.pageTitle || "vortex_video.mp4");
  const pageUrl = message.pageUrl || (sender && sender.tab ? sender.tab.url : null);
  const cookieString = await getCookiesForUrl(url);

  const payload = {
    url: url,
    fileName: suggestedFileName,
    suggestedFileName: suggestedFileName,
    pageUrl: pageUrl,
    fileSize: null,
    mime: null,
    cookies: cookieString,
    referer: pageUrl || "",
    referrer: pageUrl || "",
    userAgent: navigator.userAgent
  };

  // 1. Attempt transfer to local VortexDownloader desktop bridge
  const captured = await sendToVortex(payload);

  if (captured) {
    chrome.action.setBadgeText({ text: "⚡" });
    chrome.action.setBadgeBackgroundColor({ color: "#10B981" });
    setTimeout(() => {
      if (chrome.storage && chrome.storage.local) {
        chrome.storage.local.get({ interceptEnabled: true }, (res) => {
          syncInterceptionBadge(res ? res.interceptEnabled !== false : true);
        });
      } else {
        chrome.action.setBadgeText({ text: "" });
      }
    }, 3000);

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

async function handleInterceptLink(message, sender) {
  const url = message && message.url;
  if (!url || (!url.startsWith("http://") && !url.startsWith("https://"))) {
    return { success: false, error: "Geçersiz indirme bağlantısı" };
  }

  // 1. Check if download interception is enabled in user settings
  const isEnabled = await new Promise((resolve) => {
    if (typeof chrome !== 'undefined' && chrome.storage && chrome.storage.local) {
      chrome.storage.local.get({ interceptEnabled: true }, (items) => {
        resolve(items ? items.interceptEnabled !== false : true);
      });
    } else {
      resolve(true);
    }
  });

  if (!isEnabled) {
    fallbackUrls.add(url);
    setTimeout(() => fallbackUrls.delete(url), 10000);
    addLog("warn", "Link tıklaması yakalandı fakat ayarlarda indirme yakalama KAPALI.", url);
    return { success: false, disabled: true };
  }

  // 2. Extract cookies for url
  const cookieString = await getCookiesForUrl(url);

  const referrer = message.referrer || (sender && sender.tab ? sender.tab.url : "");
  const fileName = sanitizeFileName(message.fileName || "dosya");
  const userAgent = (typeof navigator !== 'undefined' && navigator.userAgent) ? navigator.userAgent : "";

  const payload = {
    url: url,
    fileName: fileName,
    fileSize: null,
    mime: null,
    pageUrl: referrer,
    cookies: cookieString,
    referer: referrer,
    referrer: referrer,
    userAgent: userAgent
  };

  // 3. Dispatch to Vortex desktop engine
  const captured = await sendToVortex(payload);

  if (captured) {
    if (typeof chrome !== 'undefined' && chrome.action) {
      chrome.action.setBadgeText({ text: "⚡" });
      if (chrome.action.setBadgeBackgroundColor) {
        chrome.action.setBadgeBackgroundColor({ color: "#10B981" });
      }
      setTimeout(() => {
        if (chrome.storage && chrome.storage.local) {
          chrome.storage.local.get({ interceptEnabled: true }, (res) => {
            syncInterceptionBadge(res ? res.interceptEnabled !== false : true);
          });
        } else {
          chrome.action.setBadgeText({ text: "" });
        }
      }, 3000);
    }

    addLog("success", `Link indirmesi Vortex'e başarıyla iletildi: "${fileName}"`, url);
    return { success: true, fileName: fileName };
  }

  fallbackUrls.add(url);
  setTimeout(() => fallbackUrls.delete(url), 10000);
  addLog("warn", `Link indirmesi Vortex'e aktarılamadı (masaüstü kapalı olabilir): "${fileName}"`, url);
  return { success: false, error: "VortexDownloader masaüstü köprüsüne bağlanılamadı" };
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

  const cookieString = await getCookiesForUrl(targetUrl);
  const referer = tab ? tab.url : (info.pageUrl || "");
  const payload = {
    url: targetUrl,
    fileName: null,
    fileSize: null,
    mime: null,
    pageUrl: referer,
    cookies: cookieString,
    referer: referer,
    referrer: referer,
    userAgent: navigator.userAgent
  };

  const captured = await sendToVortex(payload);

  if (captured) {
    chrome.action.setBadgeText({ text: "⚡" });
    chrome.action.setBadgeBackgroundColor({ color: "#10B981" });
    setTimeout(() => {
      if (chrome.storage && chrome.storage.local) {
        chrome.storage.local.get({ interceptEnabled: true }, (res) => {
          syncInterceptionBadge(res ? res.interceptEnabled !== false : true);
        });
      } else {
        chrome.action.setBadgeText({ text: "" });
      }
    }, 3000);

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

