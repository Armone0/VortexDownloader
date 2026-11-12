// VortexDownloader Extension Popup Controller
const VORTEX_PING_URL = "http://127.0.0.1:19741/ping";

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

  // Log Console Elements
  const logConsole = document.getElementById("logConsole");
  const btnCopyLogs = document.getElementById("btnCopyLogs");
  const btnClearLogs = document.getElementById("btnClearLogs");

  // Interception Toggle Elements
  const toggleIntercept = document.getElementById("toggleIntercept");
  const interceptStatusDesc = document.getElementById("interceptStatusDesc");
  const featureIconIntercept = document.getElementById("featureIconIntercept");
  const featureTextIntercept = document.getElementById("featureTextIntercept");

  function renderLogs(logs) {
    if (!logConsole) return;
    if (!logs || logs.length === 0) {
      logConsole.innerHTML = '<div class="log-entry info">[--:--:--] Günlük boş. İndirme denemesi bekleniyor...</div>';
      return;
    }
    logConsole.innerHTML = logs.map(l => {
      const cls = l.level || "info";
      const detailStr = l.details ? ` (${l.details})` : "";
      return `<div class="log-entry ${cls}">[${l.time || ""}] ${escapeHtml(l.message || "")}${escapeHtml(detailStr)}</div>`;
    }).join("");
    logConsole.scrollTop = logConsole.scrollHeight;
  }

  function escapeHtml(text) {
    return String(text).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
  }

  function loadLogs() {
    if (chrome.storage && chrome.storage.local) {
      chrome.storage.local.get({ debugLogs: [] }, (res) => {
        renderLogs(res ? (res.debugLogs || []) : []);
      });
    }
  }

  if (chrome.storage && chrome.storage.onChanged) {
    chrome.storage.onChanged.addListener((changes, area) => {
      if (area === "local" && changes.debugLogs) {
        renderLogs(changes.debugLogs.newValue || []);
      }
    });
  }

  if (btnCopyLogs) {
    btnCopyLogs.addEventListener("click", () => {
      if (chrome.storage && chrome.storage.local) {
        chrome.storage.local.get({ debugLogs: [] }, (res) => {
          const logs = res ? (res.debugLogs || []) : [];
          if (logs.length === 0) {
            btnCopyLogs.textContent = "Boş!";
            setTimeout(() => btnCopyLogs.textContent = "📋 Kopyala", 1500);
            return;
          }
          const text = logs.map(l => `[${l.time}] [${(l.level || 'INFO').toUpperCase()}] ${l.message}${l.details ? ' | ' + l.details : ''}`).join("\n");
          navigator.clipboard.writeText(text).then(() => {
            btnCopyLogs.textContent = "Kopyalandı! ✓";
            setTimeout(() => btnCopyLogs.textContent = "📋 Kopyala", 2000);
          }).catch(() => {
            btnCopyLogs.textContent = "Hata!";
            setTimeout(() => btnCopyLogs.textContent = "📋 Kopyala", 1500);
          });
        });
      }
    });
  }

  if (btnClearLogs) {
    btnClearLogs.addEventListener("click", () => {
      if (chrome.storage && chrome.storage.local) {
        chrome.storage.local.set({ debugLogs: [] }, () => {
          renderLogs([]);
        });
      }
    });
  }

  function updateInterceptUI(enabled) {
    if (toggleIntercept) toggleIntercept.checked = enabled;
    if (interceptStatusDesc) {
      interceptStatusDesc.textContent = enabled
        ? "Tarayıcı indirmelerini Vortex'e aktar"
        : "Devre dışı — İndirmeler tarayıcıda kalır";
      interceptStatusDesc.style.color = enabled ? "" : "var(--err)";
    }
    if (featureIconIntercept) {
      featureIconIntercept.textContent = enabled ? "✓" : "✕";
      featureIconIntercept.style.color = enabled ? "" : "var(--err)";
    }
    if (featureTextIntercept) {
      featureTextIntercept.textContent = enabled
        ? "Tarayıcı indirmelerini otomatik yakalama aktif"
        : "Tarayıcı indirmelerini otomatik yakalama duraklatıldı";
      featureTextIntercept.style.color = enabled ? "" : "var(--err)";
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

  let needsPairing = false;

  function setCheckButton(pairMode) {
    needsPairing = pairMode;
    btnCheck.textContent = pairMode ? "🔗 Uygulamayla Eşleştir" : "🔄 Bağlantıyı Sına";
  }

  async function checkBridge() {
    statusBadge.className = "status-badge checking";
    statusText.textContent = "Bağlanıyor...";
    statusTip.textContent = "Masaüstü uygulaması ile yerel köprü test ediliyor...";

    try {
      const stored = await chrome.storage.local.get({ bridgeToken: "" });
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 1800);

      const res = await fetch(VORTEX_PING_URL, {
        method: "GET",
        headers: stored.bridgeToken ? { "X-Vortex-Auth": stored.bridgeToken } : {},
        signal: controller.signal
      });
      clearTimeout(timeout);

      if (res.ok) {
        const data = await res.json().catch(() => ({}));
        if (data.paired === false) {
          statusBadge.className = "status-badge checking";
          statusText.textContent = "EŞLEŞMEDİ";
          statusTip.textContent = "Uygulama açık ama bu eklenti henüz bağlanmadı. \"Uygulamayla Eşleştir\"e bas, sonra uygulamadaki pencerede izin ver.";
          setCheckButton(true);
          return false;
        }
        statusBadge.className = "status-badge online";
        statusText.textContent = "BAĞLI";
        statusTip.textContent = `Vortex ${data.version || ""} aktif ve eşleştirildi. İndirmeler ve sağ tık menüsü hazır.`;
        setCheckButton(false);
        return true;
      } else {
        statusBadge.className = "status-badge offline";
        statusText.textContent = "HATA";
        statusTip.textContent = `Sunucu HTTP ${res.status} kodu döndürdü.`;
        return false;
      }
    } catch (err) {
      statusBadge.className = "status-badge offline";
      statusText.textContent = "KAPALI";
      let detail = "VortexDownloader.exe çalışmıyor veya port 19741 engelleniyor.";
      if (err && err.message) {
        detail = `${err.message} — VortexDownloader.exe açık mı?`;
      }
      statusTip.textContent = detail;
      return false;
    }
  }

  btnCheck.addEventListener("click", () => {
    if (needsPairing) {
      statusTip.textContent = "VortexDownloader penceresinde \"İzin ver\"e tıkla...";
      btnCheck.disabled = true;
      chrome.runtime.sendMessage({ action: "pair" }, () => {
        btnCheck.disabled = false;
        checkBridge();
        loadLogs();
      });
      return;
    }
    checkBridge();
    loadLogs();
  });

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

    // The service worker owns the bridge token (and pairs first if needed)
    chrome.runtime.sendMessage({
      action: "quick_download",
      payload: {
        url: rawUrl,
        fileName: null,
        fileSize: null,
        mime: null,
        cookies: cookieString,
        referer: "",
        referrer: "",
        userAgent: navigator.userAgent
      }
    }, (response) => {
      if (!chrome.runtime.lastError && response && response.success) {
        sendFeedback.className = "feedback success";
        sendFeedback.textContent = "Vortex indirme penceresi açıldı! ⚡";
        txtUrl.value = "";
      } else {
        sendFeedback.className = "feedback error";
        sendFeedback.textContent = "Vortex'e iletilemedi. Uygulama açık ve eşleştirilmiş mi? (Günlüğe bak)";
      }
      loadLogs();
      checkBridge();
    });
  });

  // Initial check and load on popup open
  checkBridge();
  loadLogs();
});
