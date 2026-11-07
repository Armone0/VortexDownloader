// VortexDownloader Chrome / Edge Integration Service Worker
const VORTEX_BRIDGE_URL = "http://127.0.0.1:19741/download/";

chrome.downloads.onCreated.addListener(async (downloadItem) => {
  const url = downloadItem.finalUrl || downloadItem.url;
  
  // Ignore data/blob/extension urls
  if (!url || url.startsWith("blob:") || url.startsWith("data:") || url.startsWith("chrome-extension:")) {
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
      headers: { "Content-Type": "application/json" },
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
