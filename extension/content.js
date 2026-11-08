// VortexDownloader - Floating Video & Media Grabber (Video Sniffer)
(() => {
  'use strict';

  // Guard against multiple injections into the same frame
  if (window.__vortexGrabberLoaded) return;
  window.__vortexGrabberLoaded = true;

  const MEDIA_EXTENSIONS = [
    '.mp4', '.webm', '.m3u8', '.mkv', '.mov', '.avi',
    '.flv', '.ts', '.mpd', '.m4v', '.ogv', '.3gp', '.f4v'
  ];

  const MEDIA_MIME_TYPES = [
    'video/mp4', 'video/webm', 'video/ogg', 'video/quicktime',
    'video/x-matroska', 'video/x-flv', 'video/mp2t',
    'application/x-mpegurl', 'application/vnd.apple.mpegurl',
    'application/dash+xml'
  ];

  function sanitizeFileName(name) {
    if (!name || typeof name !== 'string') return 'vortex_video';
    let clean = name.replace(/[<>:"/\\|?*\x00-\x1F]/g, '_').trim();
    if (clean.length > 70) clean = clean.substring(0, 70).trim();
    return clean || 'vortex_video';
  }

  function getSuggestedFileName(mediaUrl) {
    try {
      const parsed = new URL(mediaUrl, window.location.href);
      const pathname = parsed.pathname;
      const lastSegment = pathname.split('/').filter(Boolean).pop();
      if (lastSegment) {
        const decoded = decodeURIComponent(lastSegment);
        const lower = decoded.toLowerCase();
        for (const ext of MEDIA_EXTENSIONS) {
          if (lower.endsWith(ext) || lower.includes(ext + '?')) {
            const baseName = decoded.split('?')[0];
            return sanitizeFileName(baseName);
          }
        }
      }
    } catch (_) {}

    // Fallback to page title with .mp4 extension
    const title = sanitizeFileName(document.title);
    return `${title}.mp4`;
  }

  function isEligibleMediaUrl(url, mimeType) {
    if (!url || typeof url !== 'string') return false;
    const trimmed = url.trim();
    if (!trimmed.startsWith('http://') && !trimmed.startsWith('https://')) return false;

    // Check MIME type if provided
    if (mimeType && typeof mimeType === 'string') {
      const lowerMime = mimeType.toLowerCase();
      if (MEDIA_MIME_TYPES.some(m => lowerMime.includes(m)) || lowerMime.startsWith('video/')) {
        return true;
      }
    }

    try {
      const parsed = new URL(trimmed, window.location.href);
      const pathname = parsed.pathname.toLowerCase();

      // Check file extensions
      for (const ext of MEDIA_EXTENSIONS) {
        if (pathname.endsWith(ext) || pathname.includes(ext)) {
          return true;
        }
      }

      // Check common video streaming endpoints
      if (pathname.includes('/videoplayback') || pathname.includes('/hls/') || pathname.includes('/vod/')) {
        return true;
      }
    } catch (_) {
      return false;
    }

    return false;
  }

  function extractMediaUrlFromVideo(video) {
    // 1. Try video.currentSrc
    if (video.currentSrc && typeof video.currentSrc === 'string') {
      const current = video.currentSrc.trim();
      if (current.startsWith('http://') || current.startsWith('https://')) {
        if (isEligibleMediaUrl(current) || video.readyState > 0) {
          return current;
        }
      }
    }

    // 2. Try video.src
    if (video.src && typeof video.src === 'string') {
      const src = video.src.trim();
      if (src.startsWith('http://') || src.startsWith('https://')) {
        if (isEligibleMediaUrl(src) || video.readyState > 0) {
          return src;
        }
      }
    }

    // 3. Inspect child <source> elements
    const sources = video.querySelectorAll('source');
    for (const source of sources) {
      const src = (source.src || source.getAttribute('src') || '').trim();
      const type = (source.type || source.getAttribute('type') || '').trim();
      if (src.startsWith('http://') || src.startsWith('https://')) {
        if (isEligibleMediaUrl(src, type)) {
          return src;
        }
      }
    }

    // 4. Inspect data attributes (common in lazy-loaded players)
    const dataSrc = video.getAttribute('data-src') || video.getAttribute('data-video-url');
    if (dataSrc && (dataSrc.startsWith('http://') || dataSrc.startsWith('https://'))) {
      if (isEligibleMediaUrl(dataSrc)) {
        return dataSrc;
      }
    }

    return null;
  }

  function findAnchorContainer(video) {
    let container = video.parentElement;
    if (!container) return null;

    // In some players, the immediate parent is a zero-height wrapper
    const vRect = video.getBoundingClientRect();
    let cRect = container.getBoundingClientRect();
    if (vRect.height > 50 && cRect.height < 10 && container.parentElement && container.parentElement !== document.body) {
      container = container.parentElement;
    }

    // Ensure the container creates a positioning context
    if (container !== document.body && container !== document.documentElement) {
      const style = window.getComputedStyle(container);
      if (style.position === 'static') {
        container.classList.add('vortex-video-container-anchor');
      }
    }

    return container;
  }

  function setPillStatus(pill, textSpan, message, statusClass, defaultText) {
    if (textSpan) textSpan.textContent = message;
    pill.classList.remove('vortex-grabber-success', 'vortex-grabber-warning', 'vortex-grabber-error');
    if (statusClass) pill.classList.add(`vortex-grabber-${statusClass}`);

    setTimeout(() => {
      if (pill && pill.isConnected) {
        pill.classList.remove('vortex-grabber-success', 'vortex-grabber-warning', 'vortex-grabber-error');
        if (textSpan) textSpan.textContent = defaultText;
      }
    }, 2800);
  }

  function injectPillForVideo(video, mediaUrl) {
    if (video._vortexDismissed) return;

    // Validate size: Ignore tiny thumbnails, background tracks, or audio elements
    const rect = video.getBoundingClientRect();
    if (rect.width > 0 && rect.height > 0 && (rect.width < 140 || rect.height < 90)) {
      return;
    }

    const container = findAnchorContainer(video);
    if (!container) return;

    const videoId = video.dataset.vortexId || ('vx_' + Math.random().toString(36).substring(2, 9));
    video.dataset.vortexId = videoId;

    // Check if pill already exists in the container for this video
    let pill = container.querySelector(`.vortex-video-grabber-pill[data-vortex-id="${videoId}"]`);
    if (pill) {
      pill.dataset.vortexUrl = mediaUrl;
      return;
    }

    const suggestedFileName = getSuggestedFileName(mediaUrl);

    // Build the Floating Pill element
    pill = document.createElement('div');
    pill.className = 'vortex-video-grabber-pill';
    pill.dataset.vortexId = videoId;
    pill.dataset.vortexUrl = mediaUrl;

    const downloadBtn = document.createElement('button');
    downloadBtn.type = 'button';
    downloadBtn.className = 'vortex-grabber-btn';
    downloadBtn.title = `VortexDownloader ile İndir (${suggestedFileName})`;

    const iconSpan = document.createElement('span');
    iconSpan.className = 'vortex-grabber-icon';
    iconSpan.textContent = '⚡';

    const textSpan = document.createElement('span');
    textSpan.className = 'vortex-grabber-text';
    textSpan.textContent = 'Vortex: Videoyu İndir';

    downloadBtn.appendChild(iconSpan);
    downloadBtn.appendChild(textSpan);

    const closeBtn = document.createElement('button');
    closeBtn.type = 'button';
    closeBtn.className = 'vortex-grabber-close';
    closeBtn.title = 'Bu video için gizle';
    closeBtn.setAttribute('aria-label', 'Gizle');
    closeBtn.innerHTML = '&times;';

    // Dismiss handling
    closeBtn.addEventListener('click', (e) => {
      e.preventDefault();
      e.stopPropagation();
      video._vortexDismissed = true;
      pill.remove();
    });

    // Download action
    downloadBtn.addEventListener('click', (e) => {
      e.preventDefault();
      e.stopPropagation();

      const activeUrl = pill.dataset.vortexUrl || mediaUrl;
      const defaultLabel = 'Vortex: Videoyu İndir';

      textSpan.textContent = 'İletiliyor... ⚡';
      pill.classList.add('vortex-grabber-busy');

      chrome.runtime.sendMessage({
        action: 'download_media',
        url: activeUrl,
        suggestedFileName: suggestedFileName,
        pageTitle: document.title,
        pageUrl: window.location.href
      }, (response) => {
        pill.classList.remove('vortex-grabber-busy');

        if (chrome.runtime.lastError) {
          setPillStatus(pill, textSpan, 'Bağlantı Hatası', 'error', defaultLabel);
          return;
        }

        if (response && response.success) {
          if (response.bridge) {
            setPillStatus(pill, textSpan, "Vortex'e Aktarıldı! ⚡", 'success', defaultLabel);
          } else {
            setPillStatus(pill, textSpan, 'Tarayıcıda Başlatıldı ⚡', 'warning', defaultLabel);
          }
        } else {
          const errMsg = (response && response.error) ? response.error : 'İndirme Başarısız';
          setPillStatus(pill, textSpan, errMsg, 'error', defaultLabel);
        }
      });
    });

    pill.appendChild(downloadBtn);
    pill.appendChild(closeBtn);

    container.appendChild(pill);
  }

  function inspectAndAttach(video) {
    if (!video || video._vortexDismissed) return;

    const mediaUrl = extractMediaUrlFromVideo(video);
    if (mediaUrl) {
      injectPillForVideo(video, mediaUrl);
    }
  }

  function monitorVideo(video) {
    if (!video || video._vortexMonitored) return;
    video._vortexMonitored = true;

    const handler = () => inspectAndAttach(video);

    // Primary events as required: play and loadeddata
    video.addEventListener('play', handler);
    video.addEventListener('loadeddata', handler);
    video.addEventListener('loadedmetadata', handler);
    video.addEventListener('canplay', handler);

    // Initial inspection
    inspectAndAttach(video);
  }

  function scanAllVideos() {
    const videos = document.querySelectorAll('video');
    for (const v of videos) {
      monitorVideo(v);
    }
  }

  // Observe dynamically created videos and players
  const observer = new MutationObserver((mutations) => {
    for (const mutation of mutations) {
      for (const node of mutation.addedNodes) {
        if (node.nodeType !== Node.ELEMENT_NODE) continue;
        if (node.tagName === 'VIDEO') {
          monitorVideo(node);
        } else if (node.querySelectorAll) {
          const vList = node.querySelectorAll('video');
          for (const v of vList) {
            monitorVideo(v);
          }
        }
      }
    }
  });

  // --- Proactive IDM-Style Link Click Interceptor ---
  const DOWNLOADABLE_EXTENSIONS = [
    '.zip', '.rar', '.7z', '.tar', '.gz', '.bz2', '.xz', '.iso',
    '.exe', '.msi', '.apk', '.dmg', '.pkg', '.deb', '.rpm', '.bin', '.img',
    '.mp4', '.mkv', '.avi', '.mov', '.wmv', '.flv', '.webm',
    '.mp3', '.flac', '.wav', '.aac', '.ogg',
    '.pdf', '.docx', '.xlsx', '.pptx', '.epub', '.torrent'
  ];

  function isDownloadableLink(urlStr, linkElement) {
    if (!urlStr || typeof urlStr !== 'string') return null;
    const trimmed = urlStr.trim();
    if (!trimmed.startsWith('http://') && !trimmed.startsWith('https://')) return null;

    // Check link download attribute first
    if (linkElement && linkElement.hasAttribute('download')) {
      const dlAttr = (linkElement.getAttribute('download') || '').trim();
      if (dlAttr) {
        const lowerAttr = dlAttr.toLowerCase();
        for (const ext of DOWNLOADABLE_EXTENSIONS) {
          if (lowerAttr.endsWith(ext)) {
            return sanitizeFileName(dlAttr);
          }
        }
      }
    }

    try {
      const parsed = new URL(trimmed, window.location.href);
      const pathname = parsed.pathname.toLowerCase();
      const lastSegment = pathname.split('/').filter(Boolean).pop();

      if (lastSegment) {
        const decoded = decodeURIComponent(lastSegment);
        for (const ext of DOWNLOADABLE_EXTENSIONS) {
          if (decoded.endsWith(ext)) {
            return sanitizeFileName(decoded);
          }
        }
      }

      // Check query parameter values (e.g. ?file=setup.exe or ?download=archive.zip)
      for (const [_, val] of parsed.searchParams.entries()) {
        const lowerVal = val.toLowerCase();
        for (const ext of DOWNLOADABLE_EXTENSIONS) {
          if (lowerVal.endsWith(ext)) {
            const decodedVal = decodeURIComponent(val.split(/[\\/]/).pop() || val);
            return sanitizeFileName(decodedVal);
          }
        }
      }
    } catch (_) {}

    return null;
  }

  let activeToast = null;
  let toastTimer = null;

  function showToast(message, type = 'info', duration = 3000) {
    const parent = document.body || document.documentElement;
    if (!parent) return;

    if (!activeToast || !activeToast.isConnected) {
      activeToast = document.createElement('div');
      activeToast.className = 'vortex-toast';
      parent.appendChild(activeToast);
    }

    activeToast.textContent = message;
    activeToast.className = `vortex-toast vortex-toast-${type} vortex-toast-visible`;

    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(() => {
      if (activeToast && activeToast.isConnected) {
        activeToast.classList.remove('vortex-toast-visible');
      }
    }, duration);
  }

  function onLinkClick(e) {
    // IDM Standard Bypass: If Alt key is held down, allow native browser download
    if (e.altKey) return;

    const link = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if (!link || !link.href) return;

    const href = link.href.trim();
    if (!href.startsWith('http://') && !href.startsWith('https://')) return;

    const fileName = isDownloadableLink(href, link);
    if (!fileName) return;

    // Prevent native browser download & stop propagation
    e.preventDefault();
    e.stopPropagation();

    // Show transmitting HUD toast
    showToast(`İletiliyor... ⚡ (${fileName})`, 'info', 3000);

    chrome.runtime.sendMessage({
      action: 'intercept_link',
      url: href,
      fileName: fileName,
      referrer: window.location.href
    }, (response) => {
      if (chrome.runtime.lastError || !response || !response.success) {
        showToast(`Vortex kapalı, tarayıcıda indiriliyor (${fileName})`, 'warning', 3500);
        setTimeout(() => {
          if (link && link.getAttribute && link.getAttribute('target') === '_blank') {
            window.open(href, '_blank');
          } else {
            window.location.href = href;
          }
        }, 600);
      } else {
        showToast(`⚡ İndirme Başlatıldı (${fileName})`, 'success', 3500);
      }
    });
  }

  // Register capture-phase listener to intercept before page handlers or browser default
  document.addEventListener('click', onLinkClick, true);

  function init() {
    scanAllVideos();
    const root = document.body || document.documentElement;
    if (root) {
      observer.observe(root, { childList: true, subtree: true });
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init, { once: true });
  } else {
    init();
  }
})();
