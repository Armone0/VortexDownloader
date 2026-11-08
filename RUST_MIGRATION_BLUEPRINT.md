# 🦀 VortexDownloader — Rust Migration Blueprint & Architecture Guide

> **Target Goal:** Complete, high-fidelity migration of VortexDownloader (C# / .NET 9 / WPF) into an idiomatic, ultra-high performance **Rust** engine with native Windows shell integration and modern GUI (Tauri v2 / Slint).

---

## 📑 Table of Contents
1. [Core Engine & Architectural Mapping](#1-core-engine--architectural-mapping)
2. [Dynamic Chunk Bisection Engine](#2-dynamic-chunk-bisection-engine)
3. [Zero-Allocation Network & Disk I/O Pipeline](#3-zero-allocation-network--disk-io-pipeline)
4. [Browser Extension Bridge & Fallback Protocol](#4-browser-extension-bridge--fallback-protocol)
5. [IDM-Style Download Prompt Window](#5-idm-style-download-prompt-window)
6. [Windows Shell, Animated Tray & Taskbar Integration](#6-windows-shell-animated-tray--taskbar-integration)
7. [Persistence & Atomic State Store (ACID)](#7-persistence--atomic-state-store-acid)
8. [Recommended Rust Crates Stack](#8-recommended-rust-crates-stack)

---

## 1. Core Engine & Architectural Mapping

| .NET 9 / C# Component | Proposed Rust Equivalent | Design Highlights |
| :--- | :--- | :--- |
| `DownloadEngine` | `vortex_core::engine::Engine` | `Arc<EngineState>`, Tokio task spawn coordinator |
| `HttpDownloadTask` | `vortex_core::task::DownloadTask` | Atomic progress counters, channel-based state broadcast |
| `ArrayPool<byte>.Shared` | `bytes::BytesMut` / `deadpool` | Fixed-size preallocated 64-128 KB buffer pools |
| `SocketsHttpHandler` | `reqwest::Client` (hyper-based) | HTTP/1.1 pipelining, TCP keepalive, connection pool pooling |
| `TokenBucketRateLimiter` | `governor` / `tokio::time::Instant` | Atomic CAS token-bucket algorithm with zero jitter |
| `ZipInspector` | `vortex_core::zip::ZipInspector` | Remote HTTP Range Central Directory parser |
| `BrowserBridge` (HttpListener) | `axum` on `127.0.0.1:19741` | Asynchronous REST endpoints, CORS headers, token auth |
| `DownloadPromptWindow` (WPF) | `Tauri v2 WebviewWindow` / `Slint` | Standalone, borderless, draggable, dark loft palette |
| `VortexTrayIcon` (Shell_NotifyIcon) | `windows::Win32::UI::Shell` | `Shell_NotifyIconW`, animated 16-frame HICON buffer |
| `TaskbarItemInfo` (WPF) | `ITaskbarList3` COM Interface | Real-time progress bar state + progress value in taskbar |

---

## 2. Dynamic Chunk Bisection Engine

In C#, `FindMaxFreeChunk` identifies the worker with the largest remaining byte range and splits it in half (`bisection`), ensuring no worker is left idle while another struggles with tail-latency.

### Rust Implementation Blueprint:

```rust
use std::sync::Arc;
use tokio::sync::RwLock;

#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub enum ChunkStatus {
    Pending,
    Downloading,
    Completed,
    Failed,
}

#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct Chunk {
    pub id: usize,
    pub start_offset: u64,
    pub end_offset: u64,
    pub current_offset: u64,
    pub status: ChunkStatus,
}

pub struct DownloadTask {
    pub id: String,
    pub url: String,
    pub destination_path: std::path::PathBuf,
    pub total_bytes: u64,
    pub chunks: Vec<Chunk>,
    pub supports_range: bool,
}

impl DownloadTask {
    /// Attempts to bisect the largest in-flight chunk if remaining bytes exceed threshold (e.g. 2 MB)
    pub fn try_bisect_largest_chunk(&mut self) -> Option<Chunk> {
        if !self.supports_range {
            return None;
        }

        let mut candidate_idx = None;
        let mut max_remaining = 0u64;

        for (idx, chunk) in self.chunks.iter().enumerate() {
            if chunk.status == ChunkStatus::Downloading {
                let remaining = chunk.end_offset.saturating_sub(chunk.current_offset);
                // Threshold: Only bisect chunks that still have > 2 MB remaining
                if remaining > 2 * 1024 * 1024 && remaining > max_remaining {
                    max_remaining = remaining;
                    candidate_idx = Some(idx);
                }
            }
        }

        let idx = candidate_idx?;
        let chunk = &mut self.chunks[idx];
        let remaining = chunk.end_offset - chunk.current_offset;
        let split_offset = chunk.current_offset + (remaining / 2);
        let original_end = chunk.end_offset;

        // Truncate existing chunk
        chunk.end_offset = split_offset;

        // Spawn new chunk for the upper half
        let new_chunk = Chunk {
            id: self.chunks.len(),
            start_offset: split_offset + 1,
            end_offset: original_end,
            current_offset: split_offset + 1,
            status: ChunkStatus::Pending,
        };

        self.chunks.push(new_chunk.clone());
        Some(new_chunk)
    }
}
```

---

## 3. Zero-Allocation Network & Disk I/O Pipeline

Instead of writing to separate temporary chunk files and combining them at the end (which causes heavy I/O spikes and delays for large 50 GB+ files), Vortex uses **sparse file allocation** and direct offset writes:

```rust
use tokio::fs::{File, OpenOptions};
use tokio::io::{AsyncSeekExt, AsyncWriteExt, SeekFrom};
use std::sync::Arc;
use tokio::sync::Mutex;

pub struct SharedFileWriter {
    file: Arc<Mutex<File>>,
}

impl SharedFileWriter {
    pub async fn create_sparse(path: &std::path::Path, total_size: u64) -> std::io::Result<Self> {
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(true)
            .open(path)
            .await?;

        // Allocate total file length ahead of time (sparse on NTFS)
        file.set_len(total_size).await?;

        Ok(Self {
            file: Arc::new(Mutex::new(file)),
        })
    }

    pub async fn write_at(&self, offset: u64, data: &[u8]) -> std::io::Result<()> {
        let mut handle = self.file.lock().await;
        handle.seek(SeekFrom::Start(offset)).await?;
        handle.write_all(data).await?;
        Ok(())
    }
}
```

---

## 4. Browser Extension Bridge & Fallback Protocol

### Localhost REST API Specifications:
- **Port:** `19741`
- **Protocol:** HTTP/1.1
- **Auth Header:** `X-Vortex-Auth: vortex-local-token`
- **CORS Headers:**
  - `Access-Control-Allow-Origin: *`
  - `Access-Control-Allow-Methods: GET, POST, OPTIONS`
  - `Access-Control-Allow-Headers: Content-Type, X-Vortex-Auth`

### Fallback Protocol Sequence:
```
[User Clicks Link in Chrome/Edge]
       │
       ▼
[content.js checks: Is Alt pressed?]
       ├─ Yes ──► Bypass -> Let Browser download natively
       └─ No  ──► Send to background.js
                       │
                       ▼
             [POST http://127.0.0.1:19741/new]
                       │
                       ├─ Desktop returns HTTP 409 Conflict (URL in fallback list)?
                       │     └─► Do not intercept! Let browser handle download!
                       │
                       └─ Desktop returns HTTP 200 OK
                             └─► Show Toast: "⚡ Vortex'e Aktarıldı"
                                 Desktop shows standalone DownloadPromptWindow
```

---

## 5. IDM-Style Download Prompt Window

### Essential UI Contract (Tauri v2 or Slint):
1. **Window Properties:**
   - `decorations: false`
   - `transparent: true`
   - `always_on_top: true`
   - `center: true`
   - Dimensions: `width: 640`, `height: 580` (optimized for 864p / 1080p 125% DPI displays).
2. **Hit-Testing & Window Dragging:**
   - Any click on non-interactive elements (header background, padding) calls `app_handle.get_webview_window().start_dragging()`.
3. **Keyboard Accessibility:**
   - `Esc` triggers Cancel.
   - `Enter` triggers Immediate Start with Vortex.
4. **Category Detection Table:**
   - `.zip`, `.rar`, `.7z`, `.tar`, `.gz` -> `Downloads/Compressed`
   - `.exe`, `.msi`, `.pkg`, `.iso`, `.dmg` -> `Downloads/Programs`
   - `.mp4`, `.mkv`, `.avi`, `.mov`, `.webm` -> `Downloads/Video`
   - `.mp3`, `.flac`, `.wav`, `.aac` -> `Downloads/Music`
   - `.pdf`, `.docx`, `.xlsx`, `.epub` -> `Downloads/Documents`
   - Others -> `Downloads`

---

## 6. Windows Shell, Animated Tray & Taskbar Integration

### A. Spinning Vortex Tray Icon (`Shell_NotifyIconW`):
Pre-render 16 rotating frames of the Vortex logo at 0°, 22.5°, 45°, ..., 337.5° into native `HICON` handles.
During active downloads, run a lightweight Tokio timer at 80ms interval cycling `NIM_MODIFY`:

```rust
#[cfg(target_os = "windows")]
use windows::Win32::UI::Shell::{Shell_NotifyIconW, NOTIFYICONDATAW, NIM_MODIFY, NIF_ICON};

pub fn update_tray_frame(hwnd: isize, frame_hicon: isize) {
    let mut nid = NOTIFYICONDATAW::default();
    nid.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
    nid.hWnd = windows::Win32::Foundation::HWND(hwnd as _);
    nid.uID = 101;
    nid.uFlags = NIF_ICON;
    nid.hIcon = windows::Win32::UI::WindowsAndMessaging::HICON(frame_hicon as _);
    unsafe {
        Shell_NotifyIconW(NIM_MODIFY, &nid);
    }
}
```

### B. Taskbar Progress Indicator:
```rust
#[cfg(target_os = "windows")]
use windows::Win32::UI::Shell::{ITaskbarList3, TBPFLAG, TBPF_NORMAL};

pub fn update_taskbar_progress(taskbar: &ITaskbarList3, hwnd: isize, current: u64, total: u64) {
    unsafe {
        let hwnd = windows::Win32::Foundation::HWND(hwnd as _);
        taskbar.SetProgressState(hwnd, TBPF_NORMAL);
        taskbar.SetProgressValue(hwnd, current, total);
    }
}
```

---

## 7. Persistence & Atomic State Store (ACID)

To guarantee 100% crash and power-loss resilience, writes to `tasks.json` must be atomic:
1. Serialize tasks to memory via `serde_json::to_vec_pretty`.
2. Write buffer to a temporary file in the same directory: `tasks.json.tmp`.
3. Flush and sync file buffers to disk via `file.sync_all()`.
4. Perform an atomic filesystem rename: `std::fs::rename("tasks.json.tmp", "tasks.json")`.

---

## 8. Recommended Rust Crates Stack

```toml
[dependencies]
# Async Runtime & Networking
tokio = { version = "1.38", features = ["full"] }
reqwest = { version = "0.12", features = ["stream", "cookies"] }
bytes = "1.6"

# Serialization & Atomic State
serde = { version = "1.0", features = ["derive"] }
serde_json = "1.0"
tempfile = "3.10"

# Rate Limiting & Concurrency
governor = "0.6"
crossbeam-channel = "0.5"

# Local REST Bridge
axum = "0.7"
tower-http = { version = "0.5", features = ["cors"] }

# Zip Inspection
zip = { version = "2.1", default-features = false, features = ["deflate"] }

# GUI & Windowing
tauri = { version = "2.0", features = ["tray-icon"] }

# Windows Native APIs
[target.'cfg(windows)'.dependencies]
windows = { version = "0.58", features = [
    "Win32_Foundation",
    "Win32_UI_Shell",
    "Win32_UI_WindowsAndMessaging",
    "Win32_System_Power"
] }
```

---

*Authored for VortexDownloader Engineering Team — Clean-Room IDM Architecture Spec.*
