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

## [1.2.0] - (Planlanan Büyük Özellik Sürümü - Roadmap)
- [ ] **Video & Medya Akışı Yakalayıcı (Floating Video Grabber):** Web sayfalarında oynatılan HTML5, HLS (`.m3u8`) ve DASH video akışlarını otomatik algılayıp doğrudan indirme çubuğu sunma.
- [ ] **Akıllı ZIP Önizleme (Central Directory Peek):** Büyük ZIP arşivlerini tamamen indirmeden son birkaç kilobaytından içindekiler tablosunu okuyup tek dosya seçip indirebilme.
- [ ] **Gelişmiş Görev Zamanlayıcısı (Scheduler):** Gece belirli saatte indirmeyi otomatik başlatma ve indirme bitince bilgisayarı kapatma/uyutma modu.
- [ ] **Site Grabber / Toplu İndirme:** Sayfadaki tüm medya ve belgeleri tek tıkla kuyruğa ekleme.

---

## [1.2.1] - (Planlanan Yama ve Bugfix Sürümü)
- [ ] 1.2 sürümünde eklenecek video yakalama ve ZIP önizleme modüllerinin kararlılık testleri ve hata düzeltmeleri.
