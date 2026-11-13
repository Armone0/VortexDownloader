# 📋 VortexDownloader Değişiklik Günlüğü (Changelog)

Tüm sürüm güncellemeleri, hata düzeltmeleri ve planlanan yol haritası [SemVer](https://semver.org/) (Semantic Versioning) standardına uygun olarak bu belgede kayıt altına alınır.

---

## [Unreleased]

### Yeni
- **Oyun / tam ekran modu:** Tam ekran bir uygulama (oyun, film, sunum) çalışırken indirmeler otomatik yavaşlar (256 KB/s, 512 KB/s veya 1 MB/s; varsayılan 512 KB/s), ping etkilenmez. Tam ekrandan çıkınca kendi hız sınırın geri gelir; hız sınırı kutusunun yanında "🎮 Oyun modu" rozeti görünür. Ayarlardan kapatılabilir.

---

## [1.7.0] - 2026-11-12

### Yeni
- **ZIP içinden dosya seçerek indirme:** ZIP önizlemesinde dosyaları onay kutularıyla işaretle (tümünü seç dahil), Yeni İndirme penceresine dön ve "Seçilenleri İndir" ile yalnızca onları hedef klasöre indir (HTTP Range ile sadece o dosyaların baytları; CRC doğrulaması, zip-slip koruması, üzerine yazmama).
- **Süresi dolan linkte devam:** Link 401/403/404/410 verirse indirilen veri korunur, kartta "Bağlantıyı yenile" çıkar; sayfadan dosya yeniden indirilince Vortex aynı ad ve boyuttaki yarım indirmeyi tanır ve kaldığı yerden devam eder (boyut ve ETag/Last-Modified eşleşmesi şart).
- **Güvenlik ve bütünlük kontrolü:** İndirme bitince SHA-256 hesaplanır ve Windows Defender ile taranır; kartta "✓ Temiz" / "⚠ Tehdit bulundu" rozeti, hash'i kopyalama ve beklenen hash'le karşılaştırma. Ayarlardan kapatılabilir.

### Güvenlik
- **Eklenti eşleştirme:** Sabit paylaşılan token kaldırıldı. Uygulama kurulum başına rastgele token üretir; eklenti ilk bağlantıda uygulamada onay ister ve köprü yalnızca onaylanan eklentiyi kabul eder.

---

## [1.6.2] - 2026-11-12

### Güvenlik
- TLS sertifika doğrulaması varsayılan olarak açık; köprüde tam Host eşleşmesi, uzantı olmayan Origin'e 403, 1 MB gövde sınırı.
- Range'i yok sayan sunucuda sessiz veri bozulması yerine hata.

### Motor
- Dinamik parça bölme artık gerçekten çalışıyor; çerez/referer/user-agent kaydediliyor ve resume kontrolüne geçiriliyor.

### Arayüz
- Parça haritası, kalan süre, dosya türü kutusu, arama kutusu, hız grafiği + boş disk alanı, tamamlanınca birleşme animasyonu.
- Yeni İndirme penceresi ve YouTube paneli temaya bağlandı (Dark/Light).
- Sil: tamamlanan indirmede dosyayı diskten silme seçeneği.
- Eklentiler sayfası: veri-tabanlı (kod içermeyen) indirilebilir eklentiler, favoriler sol menüde.
- Sürüm bilgisi artık assembly'den okunuyor.
- Onay pencereleri keskin çiziliyor (gölge efekti içeriği bitmap'e çevirip yazıları bulanıklaştırıyordu).
- Bağlantı sayısı: kaydırma çubuğu yerine 1–32 arası tek satır düğme grubu; kartta "N Bağlantı" etiketi.
- Parça haritası bağlantı başına sabit dilim gösteriyor (bölünen parçalar artık onlarca noktaya dönüşmüyor).
- Silme onayı temalı pencere oldu; "dosyayı diskten de sil" seçeneği varsayılan kapalı.

### Tarayıcı Eklentisi
- `download` özellikli linklerde çökme düzeltildi (`updateBadge` tanımsızdı): dosya hem Vortex'e hem tarayıcıya gidiyor, "Vortex kapalı" uyarısı çıkıyordu.
- "Tarayıcı ile İndir" seçilince artık hata bildirimi gösterilmiyor.
- Popup okunabilirliği: daha büyük yazı, uygulamanın renk paleti, klavye odak çerçevesi.

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

## [1.6.3] - 2026-11-11 — YouTube Kalite ve Önizleme İyileştirmeleri

- Kaynakta bulunan en yüksek video çözünürlüğü metadata taramasından sonra gösterilir; 4K seçimi 2160p hedefi kullanır.
- Video kapağı 16:9 önizleme kartında daha görünür sunulur.
- YouTube paneli açılırken ve kapatılırken yumuşak kayma/opaklık animasyonu oynatır; indirme başlarken kapanır.

## [1.6.2] - 2026-11-11 — Eklenti Altyapısı ve Yenilenen YouTube Çalışma Alanı

- **Uygulama eklenti kataloğu:** Sürümlü eklenti kimliği ve medya sağlayıcı sözleşmesi eklendi; ilk yerleşik sağlayıcı YouTube.
- **YouTube çalışma alanı:** Eski medya penceresi mevcut Vortex renk çizgisini koruyan sağ panel tasarımıyla yenilendi; kalite, format, kayıt yolu, altyazı ve paralel akış kontrolleri tek panelde.
- **Daha güvenli bağlantı akışı:** Panel açılırken pano otomatik okunmaz; boş URL gönderilemez ve desteklenmeyen host reddedilir.
- **Sürüm:** 1.6.2.

## [1.6.1] - 2026-11-08 — Tepsi Simgesi Kararlılığı & Windows Görev Çubuğu ve Arayüz Animasyon Optimizasyonu

### 🛡️ Sistem Tepsisi (System Tray) Statik Simge Kararlılığı
- **Sabit Tepsi Simgesi:** Sistem tepsisindeki (`Shell_NotifyIcon`) simgenin dönmesi ve HICON kaynak tüketimi kaldırıldı; tepsi simgesi her zaman sabit ve net orijinal simge (`_defaultIcon`) olarak tutulur.
- **Dinamik Tooltip Koruması:** İndirme devam ederken anlık hız bilgisi ve indirme durumu tepsi ipucunda (`NIF_TIP`) canlı güncellenmeye devam eder.

### 🌀 Windows Görev Çubuğu & Uygulama İçi Animasyonların Korunması
- **Dönen Görev Çubuğu Simgesi (Taskbar):** Windows görev çubuğundaki pencere simgesi indirme sırasında canlı dönmeye (`_taskbarFrames`) kesintisiz devam eder.
- **Yeşil İlerleme Çubuğu (`TaskbarItemInfo`):** Windows 11 görev çubuğu butonundaki yerel ilerleme çubuğu ve durum göstergeleri (Normal, Paused, Indeterminate) tam uyumlulukla korunur.
- **Uygulama İçi Sol Üst Girdap Animasyonu:** MainWindow sol üst marka logosunun donanım hızlandırmalı dönme `Storyboard` animasyonu korunur.
- **Sürüm Güncellemesi:** Uygulama rozetleri ve yerel köprü protokolü v1.6.1 olarak etiketlendi.

---

## [1.6.0] - 2026-11-08 — Canlı Girdap (Vortex) Dönen Logo Animasyonları (3 Kritik Nokta) & Windows 11 Görev Çubuğu İlerlemesi

### 🌀 1. Uygulama İçi (MainWindow Sol Üst Marka Logosu)
- **60 FPS Canlı Rotasyon:** İndirme aktif olduğunda (`HasActiveDownloads == true`), sol üstteki Vortex logosu WPF donanım hızlandırmalı `Storyboard` / `RotateTransform` ile 2.0 saniyelik pürüzsüz girdap döngüsüne girer.
- **Yumuşak Durma:** İndirmeler bittiğinde veya duraklatıldığında storyboard sıfırlanarak orijinal açısına kilitlenir.
- **Sürüm Rozeti:** Marka başlığı yanındaki rozet `v1.6` olarak güncellendi.

### 🔔 2. Sistem Tepsisi & Gizli Simgeler (System Tray HICON Animasyonu)
- **Önceden Hesaplanmış HICON Önbelleği:** 12 açılı (30° aralıklarla) 32-bit alfa kanallı HICON çerçeveleri uygulama başlangıcında üretilir; disk veya bellek tahsisatı (allocation) olmadan sıfır CPU yüküyle döner.
- **Akıllı Durum Yönetimi:** İndirme devam ederken saat yanındaki tepsi simgesi canlı döner ve tooltip üzerinde anlık hız bilgisi (`VortexDownloader - İndiriliyor (X MB/s)`) gösterilir. İndirme bittiğinde statik simgeye döner ve zamanlayıcı tamamen durdurulur.

### 📌 3. Windows Görev Çubuğu (Taskbar Icon & Yerel İlerleme Çubuğu)
- **Dönen Görev Çubuğu Simgesi:** Windows görev çubuğundaki uygulama simgesi indirme boyunca tepsiyle eşzamanlı dönerek kullanıcının tek bakışta indirme durumunu görmesini sağlar.
- **Windows 11 Yerel İlerleme Çubuğu (`TaskbarItemInfo`):** Görev çubuğu butonunun üzerinde yeşil indirme ilerleme çubuğu (`ProgressState = Normal`) ve toplam indirme yüzdesi (`ProgressValue`) anlık olarak gösterilir; duraklatıldığında sarı moda (`Paused`), bilinmeyen boyutlarda ise marquee moduna (`Indeterminate`) geçer.

---

## [1.5.0] - 2026-11-08 — IDM Seviyesi Sıfır-Tık İndirme, Win32 Odaklanma, Canlı Tanı Konsolu ve Proaktif Bağlantı Yakalama

### ⚡ IDM Seviyesi Sıfır-Tık Otomatik Başlatma (Zero-Click Auto-Start)
- **Onay Kutusuz Doğrudan İndirme:** Kullanıcı tercihi doğrultusunda (`AutoStartWithoutPrompt`), tarayıcıdan yakalanan indirmeler onay penceresi (`DownloadPromptWindow`) göstermeden doğrudan varsayılan dizine anında başlatılır.
- **Güvenli Dosya Adı Çözümleme (`PathHelper`):** URL, Content-Disposition ve yerel dizin çakışmalarını otomatik yöneten akıllı ve sanitize edilmiş dosya adı çözümleme altyapısı entegre edildi.

### 🪟 Win32 Zorunlu Ön Plan Aktivasyonu (Win32 Foreground Window Activation)
- Windows 11/10 işletim sisteminin `SetForegroundWindow` kısıtlamalarını aşmak için `AttachThreadInput`, `BringWindowToTop`, `ShowWindowAsync` ve `SetWindowPos` P/Invoke zinciri (`WindowHelper`) entegre edildi.
- İndirme yakalandığında onay penceresi veya ana pencere arka planda/görev çubuğunda yanıp sönmek yerine tarayıcının üzerinde zorunlu olarak en öne getirilir.

### 🔌 HTTP Köprüsü Case-Insensitive JSON Serileştirme
- Farklı tarayıcı eklentisi sürümlerinden veya isteklerden gelen `camelCase` / `PascalCase` JSON gövdeleri için `PropertyNameCaseInsensitive = true` desteği eklendi; `400 Bad Request` hataları kökten çözüldü.

### 🎣 IDM Seviyesi Proaktif Bağlantı Yakalama & Çift Kanca (Double-Hook Fallback)
- Web sayfalarında tıklanan doğrudan dosya indirme bağlantıları (zip, exe, iso, tar, 7z, mp4 vb.) DOM seviyesinde proaktif olarak algılanır.
- Tarayıcı indirmesi başarısız olursa veya iptal edilirse indirmeyi güvenceye alan çift kanca mekanizması (`double-hook`) eklendi.

### 📊 Canlı Tanı & Log Konsolu (Live Diagnostic Console & Tracing)
- Eklenti popup arayüzünde son indirme olaylarını ve köprü durumunu gösteren interaktif tanı konsolu (`Live Diagnostics`) eklendi.
- Masaüstü HTTP köprüsü için anlık tracing, hata bildirimleri ve masaüstü bağlantı takibi güçlendirildi.

### 🚦 Sıralı Tarayıcı İstek Kuyruğu & Mükerrer URL Engelleme (Prompt Queue)
- Tarayıcıdan peş peşe gelen çoklu indirme isteklerinin birbirini ezmesini önlemek için sıralı işleme kuyruğu (`_promptQueue`) eklendi.
- Aynı URL için 2 saniye içinde mükerrer indirme tetiklenmesi engellendi.

### 📥 Sistem Tepsisine Küçültme (Close-to-Tray Settings)
- Pencere kapatıldığında uygulamanın tamamen sonlanması yerine arka planda sistem tepsisinde çalışmaya devam etmesi seçeneği eklendi.

---

## [1.4.0] - 2026-11-07 — İndirme Kilitlenmesi Çözümü, Çerez/Referrer İletimi ve Güvenlik Sıkılaştırması

### 🚀 Kesintisiz İndirme Yakalama (Download Interception Overhaul)
- **onDeterminingFilename Geçişi:** `chrome.downloads.onCreated` döngü ve kilitlenme sorunu çözülerek `onDeterminingFilename` API'sine geçildi.
- **Topmost Prompt Penceresi:** İndirme onay penceresi (`DownloadPromptWindow`) doğrudan en üstte (`Topmost = true`) ve odaklanmış olarak açılır; arkada kalma veya gizlenme sorunu sıfırlandı.
- **Çerez (Cookie) ve Referrer İletimi:** Oturum ve yetkilendirme gerektiren siteler (Google Drive, GitHub, Mediafire vb.) için tarayıcı çerezleri ve referrer başlıkları otomatik olarak yakalanıp masaüstü motoruna iletilir.
- **eTLD+1 Çerez Güvenlik Sınırı:** Hassas çerezlerin üçüncü taraf CDN veya farklı apex domainlere sızmasını önlemek için eTLD+1 güvenlik sınırı (`isSameSecurityDomain`) ve HTTPS->HTTP düşürme koruması getirildi.

### 🌐 Eklenti İnteraktif Popup Menüsü (Interactive Extension Popup)
- Tarayıcı araç çubuğundaki eklenti simgesine tıklandığında açılan modern popup eklendi: Canlı masaüstü bağlantı durumu (Bağlı / Çevrimdışı), hızlı URL gönderme kutusu ve tek tıkla yakalama açma/kapama anahtarı.
- Masaüstü köprüsüne hafif `/ping` uç noktası ve tarayıcı sağ tık bağlam menüsü (`contextMenu`) eklendi.

### 🛡️ İndirme Klasörü Yazma İzni Kontrolü
- Seçilen hedef klasörün yazılabilir olup olmadığı indirme başlamadan önce test edilir; salt okunur veya yetkisiz dizinlerde sessiz başarısızlıklar engellenir.

### 🎨 İki Kademeli (Two-Tier) Çakışmasız Üst Çubuk
- Üst araç çubuğu iki kademeli esnek yerleşime geçirilerek en dar pencere boyutlarında dahi kontrollerin üst üste binmesi kesin olarak engellendi.

---

## [1.3.1] - 2026-11-07 — Üst Çubuk Taşma Koruması, Responsive Wrap ve Ayarlar Buton Düzeltmesi

### 📐 Üst Çubuk Taşma ve Çakışma Düzeltmesi (Topbar Responsive Wrap)
- Pencere daraltıldığında üst araç çubuğundaki filtre başlığı ile eylem butonlarının üst üste binmesi (`overlap`) `WrapPanel` ve dinamik `Margin` optimizasyonlarıyla tamamen engellendi.
- Küçük ekran boyutlarında dahi tüm kontroller kusursuzca alt satıra kayarak görünür kalır.

### 🎨 Checkbox Padding & Görsel Boşluklar
- Modern Checkbox şablonundaki metin ile kutu arasındaki iç boşluklar (`Padding="8,0,0,0"`) düzeltildi; metinlerin kutunun üzerine binmesi engellendi.

### ⚙️ Ayarlar Penceresi Düğme Taşması Düzeltmesi
- Ayarlar penceresinin alt kısmındaki "Kaydet" ve "İptal" butonlarının alt kenara taşması `Padding` ve `Margin` dengelenmesiyle giderildi; tüm çözünürlüklerde erişilebilir kılındı.

---

## [1.3.0] - 2026-11-07 — Kristal Açık Mod, 5px İnce Scrollbar ve Üst Bar Yenilemesi

### 💎 Kristal Netliğinde Slate Açık Mod (Crystal Clear Light Theme)
- Saf süt beyazı kartlar (`#FFFFFF`), ferah Slate 50 arka plan (`#F8FAFC`), zarif kenarlıklar (`#E2E8F0`) ve yüksek kontrastlı Slate 900 metinler (`#0F172A`).
- Tüm buton stilleri ve metinler gözü yormayan yüksek kontrastlı profesyonel görünüme kavuşturuldu.

### 🎚️ 5px Ultra-Slim Modern Scrollbar
- Windows'un kaba gri kaydırma çubuğu yerine yalnızca üzerine gelindiğinde beliren 5px genişliğinde şeffaf hap scrollbar (`Themes/Styles.xaml`).
- Sol kategori menüsündeki gereksiz taşmalar ve çift scrollbar sorunları tamamen giderildi.

### 🧭 Segmented Control Pill Üst Araç Çubuğu
- Tümünü Başlat, Tümünü Duraklat ve Bitenleri Temizle butonları tek bir zarif Segmented Control kartı altında birleştirildi.
- Hız sınırı ve Bitince Kapat kontrolleri kompakt yatay dengeye oturtuldu.

### 📐 Küçük Ekran & Responsive İyileştirmeleri
- Minimum pencere boyutları 740x460 olarak ayarlandı, düşük çözünürlüklü ekranlarda taşma ve kırpılmalar engellendi.
- Görünüm & Dil sekmesinde metin kayması (TextWrapping) düzeltildi, klavye odak çerçeveleri temizlendi.

---

## [1.2.1] - 2026-11-07 — Kararlı Yama Sürümü (Soft Loft Temaları, Dil Desteği, Ayarlar & Güvenlik)

### 🎨 Lo-Fi Soft Loft Çift Tema Motoru (Dynamic Themes)
- **Koyu Mod (Dark Theme):** Sıcak Derin Kömür (`#1E2022`), Yükseltilmiş Grafit (`#282B2F`), Mat Adaçayı Yeşili (`#8EA191`), Sıcak Yulaf Krem (`#F0ECE1`).
- **Açık Mod (Light Theme):** Sıcak Yulaf Kağıt (`#F6F4EF`), Temiz Süt Beyazı (`#FFFFFF`), Doğal Adaçayı (`#7C8C7E`), Yumuşak Kömür Metin (`#2D3133`).
- **Canlı Çalışma Zamanı Geçişi:** `ThemeManager` ve `DynamicResource` sözlüğü sayesinde uygulama yeniden başlatılmadan anında tema değişimi.

### 🌐 Dinamik Çoklu Dil Desteği (LocalizationManager)
- **Türkçe 🇹🇷 & İngilizce 🇬🇧:** Arayüzün tüm metinleri, butonları, bildirimleri ve durum etiketleri anında değiştirilebilir.

### ⚙️ Motrix Esintili Ayarlar Penceresi (SettingsWindow)
- **Sekmeli Yönetim:** Genel İndirme Ayarları, Görünüm & Dil, ve Hakkında sekmeleri.
- **Kalıcı Yapılandırma (`AppSettings`):** Tercihler `%APPDATA%\VortexDownloader\settings.json` dosyasına JSON olarak kaydedilir.
- **Kuyruk ve Hız Parametreleri:** Varsayılan indirme klasörü, varsayılan eşzamanlı görev limiti ve parça sayısı ayarlanabilir.

### 🌪️ Canlı Dönen Girdap Efekti & Yeni Resmi Logo
- Aktif indirme çalışırken başlık alanında dönen dinamik girdap animasyonu (`SpinVortexStory`).
- Yeni resmi Squircle Ribbon Swirl logosu ve çok katmanlı Windows uygulama ikonu (`vortex_app.ico`, `vortex_tray.ico`, `vortex_logo.png`, `extension/icons/`).
- Slogan ve marka manifestosu: *"Veriyi bekleme; onu girdaba çek."*

### 📁 IDM Kategori Ağacı & Disk Alanı Göstergesi
- Sol kenar çubuğunda canlı sayaç rozetleriyle IDM dosya türü filtreleri: Sıkıştırılmış, Belgeler, Müzik & Ses, Programlar, Videolar.
- İndirme dizinindeki boş disk alanını anlık gösteren mini kart (`💾 C: 142 GB Boş`).

### 🔒 Loopback Köprüsü Çok Katmanlı Güvenlik & Resume Bütünlüğü
- **Host Başlığı & DNS Rebinding Koruması:** İsteklerin `Host` başlığı `127.0.0.1` ve `localhost` ile sınırlandırıldı.
- **Sec-Fetch-Site Filtreleme:** Web sitelerinden gelen izinsiz cross-site çağrıları engellemek için `Sec-Fetch-Site: cross-site` istekleri doğrudan `403 Forbidden` ile reddediliyor.
- **Paylaşımlı Kimlik Doğrulama Belirteci (`X-Vortex-Auth`):** Tarayıcı eklentisi ile masaüstü köprüsü arasında paylaşılan belirteç (`vortex-bridge-extension-auth-token`) doğrulanmadan hiçbir işlem kabul edilmiyor.
- **Dizin Aşımı (Path Traversal) ve Şema Güvenliği:** `UrlMetadataInspector.CleanFileName` ve `Uri.Scheme` (`http`/`https`) kontrolleri güçlendirildi.
- **Kaynak Değişikliği Algılama:** Duraklatılan indirmeler devam ettirilirken sunucudaki `ETag`, `Last-Modified` (RFC 7232 / RFC 9110) ve `ContentLength` kontrol edilerek bozuk birleştirme önlenir.

---

## [1.2.0] - 2026-11-07 — Kararlı Büyük Özellik Sürümü (Major Feature Release)
