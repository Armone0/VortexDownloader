# VortexDownloader ⚡

**VortexDownloader** is a high-performance, multi-threaded segmented download manager built with **.NET 9** and modern WPF. It features dynamic chunk re-allocation, a high-precision token-bucket speed limiter, crash-resilient state recovery, and zero-allocation socket streaming.

---

## 🚀 Key Features

* **Dynamic Chunk Splitting (`FindMaxFreeChunk`):** Rather than statically partitioning a file into fixed slices, VortexDownloader actively monitors in-flight chunks and dynamically divides remaining spans in half on the fly. This guarantees full line-speed saturation and completely eliminates tail-end latency.
* **Zero-Allocation Socket Streaming:** Utilizes .NET 9 `SocketsHttpHandler` with `ArrayPool<byte>.Shared` memory pooling (80 KB chunks) to stream network bytes straight to disk without GC pressure.
* **Token Bucket Speed Limiter:** Microsecond-precision rate limiter allowing exact bandwidth throttling (e.g., 1 MB/s, 5 MB/s, or unlimited).
* **Crash-Resilient State Persistence:** Download metadata and chunk boundaries are atomically synced to `%APPDATA%\VortexDownloader\tasks.json`. If a download is interrupted by network drop or system restart, it resumes seamlessly from disk-verified offsets without re-downloading a single byte.
* **Optimized File Assembly:** Moves the primary chunk directly into the target file (`MoveFile` / fast rename) and sequential-appends subsequent chunks using pooled buffers, minimizing disk write amplification.
* **Modern Dark UI:** Minimalist, sleek Windows 11 Dark Mode design with real-time download cards, chunk indicators, and bandwidth graphs.

---

## 🛠️ Architecture

```
VortexDownloader/
├── src/
│   ├── VortexDownloader.Core/          # Core download engine
│   │   ├── Models/                     # DownloadTask, DownloadChunk, DownloadStatus
│   │   ├── Network/                    # ChunkDownloader with SocketsHttpHandler
│   │   ├── Common/                     # ChunksManager, SpeedLimiter, FileAssembler
│   │   ├── Storage/                    # StateStore (JSON persistence)
│   │   └── Engine/                     # DownloadEngine coordinator & URL probe
│   └── VortexDownloader.UI/            # Modern WPF Desktop Interface
│       ├── ViewModels/                 # MVVM ViewModels & RelayCommands
│       └── Views/                      # MainWindow & NewDownloadDialog
└── tests/
    └── VortexDownloader.Tests/         # 14 unit tests covering dynamic splitting,
                                        # speed limiting, file assembly, and state store
```

---

## 🧪 Building & Running Tests

### Prerequisites
* **.NET 9.0 SDK** (or higher)

### Build
```bash
dotnet build -c Release
```

### Run Tests
```bash
dotnet test
```
All 14 tests pass 100% verifying:
* Dynamic range splitting math
* Token bucket rate limiting accuracy
* Multi-chunk mock assembly byte fidelity
* JSON state serialization and crash recovery
* End-to-end multi-range HTTP download

---

## 📜 Legal & Clean-Room Compliance

This repository is an independent **Clean-Room implementation** developed in accordance with RFC 9110 (HTTP Semantics) and RFC 7233 (Range Requests). 
- It contains **NO** proprietary binaries, disassemblies, or assets from Tonec Inc. or third parties.
- Please see [DISCLAIMER.md](DISCLAIMER.md) for the complete legal notice.

## 📄 License
Licensed under the [Apache License 2.0](LICENSE).
