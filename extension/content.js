// VortexDownloader - Smart Video & Media Grabber (Video Sniffer)
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

  // ============================================================================
  // SITE CONFIGURATION — Site özel selector'lar ve ayarlar
  // ============================================================================
  const SITE_CONFIG = {
    'youtube.com': {
      videoSelector: '#movie_player video, .html5-video-player video',
      containerSelector: '#movie_player, .html5-video-player',
      ignoreSelectors: ['.ytp-ad-player-overlay', '.video-ads', '.ytp-chrome-bottom'],
      pillPosition: 'top-right',
    },
    'youtu.be': {
      videoSelector: '#movie_player video, .html5-video-player video',
      containerSelector: '#movie_player, .html5-video-player',
      ignoreSelectors: ['.ytp-ad-player-overlay', '.video-ads', '.ytp-chrome-bottom'],
      pillPosition: 'top-right',
    },
    'twitch.tv': {
      videoSelector: '[data-a-target="video-player"] video, .video-player__container video',
      containerSelector: '[data-a-target="video-player"], .video-player__container',
      pillPosition: 'bottom-right',
    },
    'netflix.com': {
      videoSelector: '.watch-video video, video.nf-player-core',
      containerSelector: '.watch-video, .player-wrapper',
      pillPosition: 'top-right',
    },
    'vimeo.com': {
      videoSelector: '.player video, #player video',
      containerSelector: '.player, #player',
      pillPosition: 'top-left',
    },
    'twitter.com': {
      videoSelector: '[data-testid="videoPlayer"] video',
      containerSelector: '[data-testid="videoPlayer"]',
      pillPosition: 'top-right',
    },
    'x.com': {
      videoSelector: '[data-testid="videoPlayer"] video',
      containerSelector: '[data-testid="videoPlayer"]',
      pillPosition: 'top-right',
    },
    'linkedin.com': {
      videoSelector: '.feed-shared-video video, .video-player video',
      containerSelector: '.feed-shared-video, .video-player',
      pillPosition: 'top-right',
    },
    'facebook.com': {
      videoSelector: '[data-pagelet="video_player"] video, .x1lliihq video',
      containerSelector: '[data-pagelet="video_player"], .x1lliihq',
      pillPosition: 'top-right',
    },
    'instagram.com': {
      videoSelector: 'video[playsinline]',
      containerSelector: 'video[playsinline]',
      pillPosition: 'top-right',
    },
    '*': { // Default fallback
      videoSelector: 'video',
      containerSelector: null,
      ignoreSelectors: [],
      pillPosition: 'top-right',
    }
  };

  function getSiteConfig() {
    const hostname = window.location.hostname.replace(/^www\./, '');
    for (const [domain, config] of Object.entries(SITE_CONFIG)) {
      if (domain === '*' || hostname === domain || hostname.endsWith('.' + domain)) {
        return config;
      }
    }
    return SITE_CONFIG['*'];
  }

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

  // ============================================================================
  // ELIGIBILITY CHECKS — Sadece gerçek, izlenebilir videolar
  // ============================================================================
  function isInAdFrame(element) {
    const adFramePatterns = [
      'googleads', 'doubleclick', 'googlesyndication', 'amazon-adsystem',
      'adsystem', 'adnxs', 'rubiconproject', 'pubmatic', 'criteo',
      'openx', 'indexexchange', 'sonobi', 'smartadserver',
      'adsafeprotected', 'iasds', 'moatads', 'integralads'
    ];
    try {
      let frame = element.ownerDocument.defaultView.frameElement;
      while (frame) {
        const src = (frame.src || '').toLowerCase();
        if (adFramePatterns.some(p => src.includes(p))) return true;
        frame = frame.ownerDocument?.defaultView?.frameElement;
      }
    } catch (_) {}
    return false;
  }

  function isHlsMasterOrSegment(url) {
    if (!url) return false;
    const lower = url.toLowerCase();
    // Master playlist usually doesn't have direct media, segments are .ts/.m4s
    return lower.includes('.ts?') || lower.includes('.m4s') || lower.endsWith('.ts') || lower.endsWith('.m4s');
  }

  function isEligibleVideo(video) {
    // 1. Boyut kontrolü: minimum 320x180
    const rect = video.getBoundingClientRect();
    if (rect.width < 320 || rect.height < 180) return false;

    // 2. Görünürlük
    const style = window.getComputedStyle(video);
    if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') return false;
    if (rect.width === 0 || rect.height === 0) return false;

    // 3. Viewport içinde mi? (en az %50)
    const vw = window.innerWidth, vh = window.innerHeight;
    const visibleW = Math.min(rect.right, vw) - Math.max(rect.left, 0);
    const visibleH = Math.min(rect.bottom, vh) - Math.max(rect.top, 0);
    if (visibleW * visibleH < rect.width * rect.height * 0.5) return false;

    // 4. Ad/Tracking iframe
    if (isInAdFrame(video)) return false;

    // 5. Video metadata yüklendi mi?
    if (video.readyState < 1) return false; // HAVE_METADATA

    // 6. HLS master playlist / segment filtreleme
    if (isHlsMasterOrSegment(video.currentSrc)) return false;

    // 7. Site ignore selector'ları
    const config = getSiteConfig();
    if (config.ignoreSelectors?.length) {
      for (const sel of config.ignoreSelectors) {
        if (video.closest(sel)) return false;
      }
    }

    return true;
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

  function findContainer(video, config) {
    if (config.containerSelector) {
      return video.closest(config.containerSelector) || video.parentElement;
    }
    return video.parentElement;
  }

  // ============================================================================
  // PERSISTENT DISMISS — Session storage per video URL
  // ============================================================================
  function getDismissedVideos() {
    try {
      return new Set(JSON.parse(sessionStorage.getItem('vortex_dismissed_videos') || '[]'));
    } catch { return new Set(); }
  }

  function dismissVideo(video, mediaUrl) {
    const key = mediaUrl + '|' + window.location.origin;
    const dismissed = getDismissedVideos();
    dismissed.add(key);
    sessionStorage.setItem('vortex_dismissed_videos', JSON.stringify([...dismissed]));
    video._vortexDismissed = true;
  }

  function isDismissed(mediaUrl) {
    const key = mediaUrl + '|' + window.location.origin;
    return getDismissedVideos().has(key);
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

    // Eligibility check — sadece gerçek videolar
    if (!isEligibleVideo(video)) return;

    // Dismissed check
    if (isDismissed(mediaUrl)) return;

    const config = getSiteConfig();
    const container = findContainer(video, config);
    if (!container) return;

    const videoId = video.dataset.vortexId || ('vx_' + Math.random().toString(36).substring(2, 9));
    video.dataset.vortexId = videoId;

    // Container'ı işaretle (CSS hover için)
    container.classList.add('vortex-video-container');

    // Check if pill already exists
    let pill = container.querySelector(`.vortex-video-grabber-pill[data-vortex-id="${videoId}"]`);
    if (pill) {
      pill.dataset.vortexUrl = mediaUrl;
      return;
    }

    const suggestedFileName = getSuggestedFileName(mediaUrl);

    // Build the Floating Pill element
    pill = document.createElement('div');
    pill.className = `vortex-video-grabber-pill vortex-pill-${config.pillPosition}`;
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
    closeBtn.title = 'Bu video için gizle (oturum boyunca)';
    closeBtn.setAttribute('aria-label', 'Gizle');
    closeBtn.innerHTML = '&times;';

    // Dismiss handling — persistent
    closeBtn.addEventListener('click', (e) => {
      e.preventDefault();
      e.stopPropagation();
      dismissVideo(video, mediaUrl);
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

    // Video oynatıldığında kısa süre göster (auto-hide animation)
    const showOnPlay = () => {
      pill.classList.add('vortex-visible');
      video.removeEventListener('play', showOnPlay);
    };
    video.addEventListener('play', showOnPlay, { once: true });

    container.appendChild(pill);
  }

  function inspectAndAttach(video) {
    if (!video || video._vortexDismissed) return;

    const mediaUrl = extractMediaUrlFromVideo(video);
    if (mediaUrl) {
      injectPillForVideo(video, mediaUrl);
    }
  }

  // ============================================================================
  // OPTIMIZED OBSERVERS — IntersectionObserver + MutationObserver
  // ============================================================================
  const videoObserver = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (entry.isIntersecting && entry.target.tagName === 'VIDEO') {
        inspectAndAttach(entry.target);
        videoObserver.unobserve(entry.target);
      }
    }
  }, { rootMargin: '200px', threshold: 0.01 });

  const mutationObserver = new MutationObserver((mutations) => {
    for (const mutation of mutations) {
      for (const node of mutation.addedNodes) {
        if (node.nodeType !== Node.ELEMENT_NODE) continue;
        if (node.tagName === 'VIDEO') {
          videoObserver.observe(node);
        } else if (node.querySelectorAll) {
          const vList = node.querySelectorAll('video');
          for (const v of vList) {
            videoObserver.observe(v);
          }
        }
      }
    }
  });

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

  // ============================================================================
  // LAYER 1: DOWNLOAD ATTRIBUTE — Her zaman yakala (passive, bubbling phase)
  // ============================================================================
  document.addEventListener('click', (e) => {
    const link = e.target.closest?.('a[href][download]');
    if (!link) return;

    const href = link.href.trim();
    if (!href.startsWith('http://') && !href.startsWith('https://')) return;

    // IDM Standard Bypass: Alt key = native browser download
    if (e.altKey) return;

    const fileName = (link.getAttribute('download') || '').trim() || sanitizeFileName(href.split('/').pop() || 'dosya');

    e.preventDefault();
    e.stopPropagation();

    showToast(`İletiliyor... ⚡ (${fileName})`, 'info', 3000);

    chrome.runtime.sendMessage({
      action: 'intercept_link',
      url: href,
      fileName: fileName,
      referrer: window.location.href,
      source: 'download-attr'
    }, (response) => {
      if (chrome.runtime.lastError || !response?.success) {
        showToast(`Vortex kapalı, tarayıcıda indiriliyor (${fileName})`, 'warning', 3500);
        setTimeout(() => window.location.href = href, 600);
      } else {
        showToast(`⚡ İndirme Başlatıldı (${fileName})`, 'success', 3500);
      }
    });
  }, false); // bubbling phase, NOT capture

  function init() {
    // Initial videos — observe via IntersectionObserver
    document.querySelectorAll('video').forEach(v => videoObserver.observe(v));

    const root = document.body || document.documentElement;
    if (root) {
      mutationObserver.observe(root, { childList: true, subtree: true });
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init, { once: true });
  } else {
    init();
  }
})();
