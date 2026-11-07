// VortexDownloader Extension Popup Controller
const VORTEX_PING_URL = "http://127.0.0.1:19741/ping";
const VORTEX_DOWNLOAD_URL = "http://127.0.0.1:19741/download/";

document.addEventListener("DOMContentLoaded", () => {
  const statusBadge = document.getElementById("statusBadge");
  const statusText = document.getElementById("statusText");
  const statusTip = document.getElementById("statusTip");
  const btnCheck = document.getElementById("btnCheckConnection");
  const btnSend = document.getElementById("btnSendToVortex");
  const txtUrl = document.getElementById("txtDownloadUrl");
  const sendFeedback = document.getElementById("sendFeedback");

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
        statusTip.textContent = `Vortex motoru aktif (${data.version || "v1.3.1"}). İndirmeler ve sağ tık menüsü hazır.`;
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

    try {
      const res = await fetch(VORTEX_DOWNLOAD_URL, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Vortex-Auth": "vortex-bridge-extension-auth-token"
        },
        body: JSON.stringify({
          url: rawUrl,
          fileName: null
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
