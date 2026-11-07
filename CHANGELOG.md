# 📋 VortexDownloader Değişiklik Günlüğü (Changelog)

Tüm sürüm güncellemeleri, hata düzeltmeleri ve planlanan yol haritası [SemVer](https://semver.org/) (Semantic Versioning) standardına uygun olarak bu belgede kayıt altına alınır.

---

## [1.1.0] - 2026-11-07 — Mevcut Kararlı Sürüm (Stable Release)

### 🛡️ Güvenlik ve Bellek Sızıntısı Düzeltmeleri (Security & Memory Audit)
- **Localhost Bridge CORS/CSRF Koruması:** `*` joker karakteri kaldırılarak sadece yetkili tarayıcı eklentileri (`chrome-extension://`, `edge-extension://`, `moz-extension://`) kabul edilecek şekilde sınırlandırıldı. Yetkisiz web sitelerine `403 Forbidden` dönülüyor.
- **Path Traversal & RCE Engellemesi:** İndirme dosya adları `SanitizeFileName` ve `Path.GetFullPath` sınır doğrulamasıyla temizlendi; dizin dışına kaçış (`../`) ve tehlikeli karakterler engellendi.
- **SSRF Önlemi:** Sadece geçerli `http` ve `https` şemaları kabul edilecek şekilde şema doğrulaması getirildi.
- **Win32 HMENU & HICON Handle Sızıntısı:** Sistem tepsisi menüsü `DestroyMenu` ve `DestroyIcon` P/Invoke çağrılarıyla `try/finally` bloklarına alındı; GDI kaynak sızıntısı sıfırlandı.
- **WPF SolidColorBrush GC Churn:** Sık oluşturulan fırçalar `static readonly` ve `Freeze()` edilerek Garbage Collector yükü minimize edildi.
- **Eşzamanlı Parça Yarış Durumu (Race Condition):** `DownloadTask.Chunks` koleksiyonu `ChunksLock` nesnesiyle iş parçacığı güvenli (thread-safe) hale getirildi.
- **Single-File Exe Çökmesi:** Asset dosyaları `.csproj` içinde `Content` yerine `Resource` (gömülü derleme kaynağı) yapılarak tek dosya (`PublishSingleFile=true`) çalıştırılmasında oluşan `path1 is null` çökmesi giderildi.

### 🎨 Profesyonel Arayüz Yenilemesi (UI/UX Overhaul)
- **2 Sütunlu Masaüstü Mimarisi:** Modern Motrix / Linear / Raycast standartlarında sol kenar çubuğu (Sidebar) ve sağ çalışma alanı ayrımı.
- **İnteraktif Kategori Filtreleme:** Sol menüden `📥 Tüm Görevler`, `⚡ İndirilenler`, `⏸ Duraklatılanlar` ve `✅ Tamamlananlar` butonlarıyla anında canlı liste filtreleme.
- **Koyu Temalı ComboBox:** Windows'un eski beyaz açılır kutusu tamamen XAML şablonuyla koyu temaya (`#131B2A`) uyarlandı; açılmama sorunu çözüldü.
- **Windows 11 Immersive Dark Mode:** DWM Win32 API ile yerel pencere başlık çubuğu ve butonları işletim sistemi koyu moduna entegre edildi.
- **Zarif Boş Durum (Empty State):** Liste boşken devasa hantal kutular yerine sade, modern ve açıklayıcı karşılama alanı yerleştirildi.

### ⚡ Ağ ve İndirme Motoru
- **Modern Tarayıcı Kimliği (User-Agent):** `VortexDownloader/1.0` başlığı engellenen siteler için modern `Chrome/130.0.0.0` kimliğiyle değiştirildi.
- **Ayrıcalıksız Port Geçişi:** Eklenti köprüsü yönetici izni gerektiren port 1001'den `19741` portuna taşındı.
- **HTTP 429 (Rate Limit) Backoff:** Çok parçalı isteklerde sunucu hız sınırına takılmamak için katlanarak artan yeniden deneme (exponential backoff retry) eklendi.
- **Hata Açıklama İpucu (Tooltip):** Hata alan görevlerde kırmızı rozetin üzerine gelindiğinde sunucudan dönen hata metni görüntülenebiliyor.

---

## [1.2.0] - 2026-11-07 — Kararlı Büyük Özellik Sürümü (Major Feature Release)

### 📦 Akıllı ZIP Önizleme (Remote Central Directory Peek)
- **Sıfır İndirmeli Arşiv İnceleme (`ZipInspector`):** Büyük ZIP arşivlerini tamamen indirmeden, HTTP Range istekleriyle dosyanın son 65 KB'ından EOCD (`0x06054b50`) ve Central Directory (`0x02014b50`) başlıklarını okuma yeteneği eklendi.
- **İçerik Tablosu Arayüzü (`ZipPreviewWindow`):** Arşiv içindeki dosyaların adları, sıkıştırılmış/sıkıştırılmamış boyutları, sıkıştırma oranları ve değiştirilme tarihleri modern bir pencerede anında listeleniyor.
- **Güvenlik & Sınır Koruması:** uint32 taşması (overflow), bozuk EOCD işaretçileri, 100 MB üstü anormal dizin başlıkları ve Range desteklemeyen sunucularda 200 OK gövde şişmesi engellendi.

### 🚦 Akıllı Kuyruk Yöneticisi & Eşzamanlılık (Queue Manager)
- **Eşzamanlı İndirme Sınırı (`MaxConcurrentDownloads`):** Kullanıcı aynı anda kaç dosyanın indirileceğini belirleyebiliyor (1, 2, 3, 5 Görev veya Sınırsız). Varsayılan: 3.
- **Otomatik FIFO Promosyonu (`ProcessDownloadQueue`):** Bir görev tamamlandığında, duraklatıldığında veya iptal edildiğinde kuyruktaki (`Queued`) en eski indirme otomatik olarak başlatılıyor.
- **İş Parçacığı & Yarış Durumu Koruması:** `_queueLock` ile tamamlanmış görevlerin duraklatma veya yeniden başlatma sırasında durumlarının ezilmesi ve bellek sızıntıları önlendi.

### ⚡ Otomatik Kapatma & Uyku Modu (Auto-Shutdown Manager)
- **İndirme Bitince Kapatma (`ShutdownManager`):** Kullanıcı üst araç çubuğundaki *"⚡ Bitince Kapat"* seçeneğini işaretlediğinde, aktif tüm indirmeler tamamlandığında güvenli Win32 çağrılarıyla bilgisayarı kapatma/uykuya alma desteği eklendi.
- **Sıfır Komut Enjeksiyonu:** Tamamen güvenli sabit argümanlarla çalışır; kullanıcı girdisi çalıştırılmaz.

### 🎥 Tarayıcı Yüzen Video Yakalayıcı (Floating Video Sniffer)
- **Dinamik Medya Algılama (`content.js`):** Web sayfalarındaki `<video>`, `<source>`, `.mp4`, `.webm`, `.m3u8` video akışlarını otomatik tespit eden DOM gözlemcisi entegre edildi.
- **Yüzen İndirme Butonu:** Video oynatıcısının üzerinde zarif, modern karanlık temalı *"⚡ Vortex: Videoyu İndir"* butonu gösteriliyor.
- **Manifest V3 Uyumluluğu:** Gereksiz izinler temizlendi (`storage` kaldırıldı), performans ve bellek optimizasyonu yapıldı.

### 📋 Toplu İndirme Desteği (Batch Download)
- **Çoklu URL Girişi:** Yeni indirme penceresinde çok satırlı veya virgülle ayrılmış URL listeleri yapıştırıldığında tüm bağlantılar otomatik olarak kuyruğa ekleniyor.

---

## [1.2.x] - (Kullanıcı Geri Bildirimi & Yama Sürümleri)
- [ ] Kullanıcının bulacağı veya bildireceği hata ve iyileştirmeler `1.2.1`, `1.2.2` şeklinde yama sürümleri olarak yayınlanacaktır.

---

## [1.1.0] - 2026-11-07 — Önceki Kararlı Sürüm
