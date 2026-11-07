// VortexDownloader Extension Popup Controller
const VORTEX_PING_URL = "http://127.0.0.1:19741/ping";
const VORTEX_DOWNLOAD_URL = "http://127.0.0.1:19741/download/";

// Extract cookies for authenticated quick downloads (symmetric to background.js)
async function getCookiesForUrl(url) {
  if (!url || typeof url !== 'string' || (!url.startsWith('http://') && !url.startsWith('https://'))) {
    return null;
  }
  try {
    if (!chrome.cookies || !chrome.cookies.getAll) return null;
    const cookies = await chrome.cookies.getAll({ url: url });
    if (cookies && cookies.length > 0) {
      return cookies.map(c => `${c.name}=${c.value}`).join('; ');
    }
  } catch (err) {
    console.warn('[Vortex] Failed to extract cookies in popup for:', url, err);
  }
  return null;
}

document.addEventListener("DOMContentLoaded", () => {
  const statusBadge = document.getElementById("statusBadge");
  const statusText = document.getElementById("statusText");
  const statusTip = document.getElementById("statusTip");
  const btnCheck = document.getElementById("btnCheckConnection");
  const btnSend = document.getElementById("btnSendToVortex");
  const txtUrl = document.getElementById("txtDownloadUrl");
  const sendFeedback = document.getElementById("sendFeedback");

  // Interception Toggle Elements
  const toggleIntercept = document.getElementById("toggleIntercept");
  const interceptStatusDesc = document.getElementById("interceptStatusDesc");
  const featureIconIntercept = document.getElementById("featureIconIntercept");
  const featureTextIntercept = document.getElementById("featureTextIntercept");

  function updateInterceptUI(enabled) {
    if (toggleIntercept) toggleIntercept.checked = enabled;
    if (interceptStatusDesc) {
      interceptStatusDesc.textContent = enabled
        ? "Tarayıcı indirmelerini Vortex'e aktar"
        : "Devre dışı — İndirmeler tarayıcıda kalır";
      interceptStatusDesc.style.color = enabled ? "#9CA3AF" : "#EF4444";
    }
    if (featureIconIntercept) {
      featureIconIntercept.textContent = enabled ? "✓" : "✕";
      featureIconIntercept.style.color = enabled ? "#10B981" : "#EF4444";
    }
    if (featureTextIntercept) {
      featureTextIntercept.textContent = enabled
        ? "Tarayıcı indirmelerini otomatik yakalama aktif"
        : "Tarayıcı indirmelerini otomatik yakalama duraklatıldı";
      featureTextIntercept.style.color = enabled ? "#9CA3AF" : "#EF4444";
    }
  }

  // Load saved interception state (default: true)
  if (chrome.storage && chrome.storage.local) {
    chrome.storage.local.get({ interceptEnabled: true }, (items) => {
      updateInterceptUI(items ? items.interceptEnabled !== false : true);
    });
  }

  // Handle toggle change event
  if (toggleIntercept) {
    toggleIntercept.addEventListener("change", () => {
      const isEnabled = toggleIntercept.checked;
      if (chrome.storage && chrome.storage.local) {
        chrome.storage.local.set({ interceptEnabled: isEnabled }, () => {
          updateInterceptUI(isEnabled);
        });
      } else {
        updateInterceptUI(isEnabled);
      }
    });
  }

  async function checkBridge() {
    statusBadge.className = "status-badge checking";
    statusText.textContent = "Bağlanıyor...";
    statusTip.textContent = "Masaüstü uygulaması ile yerel köprü test ediliyor...";

    try {
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 1800);

      const res = await fetch(VORTEX_PING_URL, {
        method: "GET",
        signal: controller.signal
      });
      clearTimeout(timeout);

      if (res.ok) {
        const data = await res.json().catch(() => ({}));
        statusBadge.className = "status-badge online";
        statusText.textContent = "BAĞLI";
        statusTip.textContent = `Vortex motoru aktif (${data.version || "v1.4.0"}). İndirmeler ve sağ tık menüsü hazır.`;
        return true;
      }
    } catch (_) {}

    statusBadge.className = "status-badge offline";
    statusText.textContent = "KAPALI";
    statusTip.textContent = "Masaüstü uygulaması kapalı görünüyor. Lütfen VortexDownloader.exe'yi çalıştırın.";
    return false;
  }

  btnCheck.addEventListener("click", checkBridge);

  btnSend.addEventListener("click", async () => {
    const rawUrl = txtUrl.value.trim();
    if (!rawUrl || (!rawUrl.startsWith("http://") && !rawUrl.startsWith("https://"))) {
      sendFeedback.className = "feedback error";
      sendFeedback.textContent = "Lütfen geçerli bir http/https bağlantısı girin.";
      return;
    }

    sendFeedback.className = "feedback";
    sendFeedback.textContent = "Vortex'e iletiliyor...";

    // Extract cookies for the URL via unified helper
    const cookieString = await getCookiesForUrl(rawUrl);

    try {
      const res = await fetch(VORTEX_DOWNLOAD_URL, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Vortex-Auth": "vortex-bridge-extension-auth-token"
        },
        body: JSON.stringify({
          url: rawUrl,
          fileName: null,
          fileSize: null,
          mime: null,
          cookies: cookieString,
          referer: "",
          referrer: "",
          userAgent: navigator.userAgent
        })
      });

      if (res.ok) {
        sendFeedback.className = "feedback success";
        sendFeedback.textContent = "Vortex indirme penceresi açıldı! ⚡";
        txtUrl.value = "";
      } else {
        sendFeedback.className = "feedback error";
        sendFeedback.textContent = `Hata: Sunucu ${res.status} kodu döndü.`;
      }
    } catch (e) {
      sendFeedback.className = "feedback error";
      sendFeedback.textContent = "Vortex masaüstü uygulamasına ulaşılamadı.";
    }
  });

  // Initial check on popup open
  checkBridge();
});
