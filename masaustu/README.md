# Stok Takip – Masaüstü Sürümü (Visual Studio 2022)

Tek bilgisayarda çalışan Windows programı. C# / .NET 8 / WinForms, veritabanı SQLite (tek dosya, ayrı sunucu gerekmez).

## Açma ve çalıştırma
1. Visual Studio 2022'yi açın → **Open a project or solution** → `masaustu/StokTakip.sln`.
   - Visual Studio Installer'da **".NET desktop development"** iş yükü yüklü olmalı.
2. Üstte başlangıç projesi **StokTakip.Masaustu** seçili olsun → **F5**.
3. İlk açılışta veritabanı oluşturulur. Giriş: **admin / admin123** → şifre değiştirmeniz istenir.

NuGet paketleri (Microsoft.Data.Sqlite, xUnit) ilk derlemede otomatik iner.

## Kurulum dosyası çıkarma (programı başka bilgisayara vermek için)
Solution Explorer → **StokTakip.Masaustu** sağ tık → **Publish** → Folder →
Deployment mode: *Self-contained*, Target runtime: *win-x64*, "Produce single file" işaretli.
Çıkan `StokTakip.exe` .NET kurulu olmayan bilgisayarda da çalışır.

Komut satırından: `dotnet publish StokTakip.Masaustu -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`

## Proje yapısı
| Proje | İçerik |
|---|---|
| `StokTakip.Core` | Veritabanı, iş kuralları (stok hareketi, talep akışı, yetkiler, raporlar, CSV). Arayüzden bağımsız. |
| `StokTakip.Masaustu` | WinForms ekranları. Formlar kodla oluşturulur (`Forms/` klasörü). |
| `StokTakip.Tests` | Core için birim testleri (Test Explorer → Run All). |

Yeni bir iş kuralı eklerken önce `Core`'a ekleyip test yazın, sonra ekrana bağlayın.

## Veriler nerede?
`C:\ProgramData\StokTakip\` klasöründe:
- `stok.db` – veritabanı
- `uploads\` – fatura, datasheet, teknik resim, fotoğraf ekleri
- `yedek\` – **program her gün ilk kapanışta otomatik yedek alır** (60 günden eskiler silinir)
- `ayarlar.json` – yollar ve firma adı (giriş ekranındaki "Ayarlar" bağlantısından değişir)

Bilgisayarı birden fazla Windows kullanıcısı kullanacaksa Ayarlar'dan herkesin yazabildiği bir klasör seçin.
Yedek klasörünü ayrıca USB / ağ diskine kopyalamanız önerilir. Yönetici menüsünde **💾 Yedek Al** ile elle yedek alınabilir.

## Ekranlar
- **Ana Sayfa**: kritik stok, bugünkü giriş/çıkış, bekleyen talepler, stok değeri (yetkiye göre)
- **Ürünler**: marka, model, kategori, lokasyon, tedarikçi filtreleri; ürün kartında lokasyon stoku, dosyalar, hareketler, satın alma geçmişi
- **Giriş (F2) / Çıkış (F3) / Transfer (F4) / Sayım**: barkod okuyucu ile çalışır (Enter → miktar)
- **Malzeme Talepleri (F6)**: talep → onay → raftan teslim (kısmi teslim)
- **Lokasyonlar**: Depo › Bölge › Raf › Göz ağacı, seri raf oluşturma
- **Tedarikçiler**, **Raporlar** (tüketim, sipariş önerisi, stok değeri, hareketsiz stok, denetim kaydı), **Kullanıcılar** (rol şablonları)
- F5: ekranı yenile

## Web sürümüyle ilişkisi
Veritabanı yapısı ve şifre biçimi web sürümüyle **birebir aynıdır** (testlerle doğrulanır).
İleride birden fazla bilgisayar/telefon gerekirse `stok.db` ve `uploads` klasörünü web sürümünün klasörüne kopyalamanız yeterli; kullanıcılar aynı şifrelerle giriş yapar.

Not: Aynı `stok.db` dosyasını ağ paylaşımından birden fazla bilgisayarla aynı anda açmayın (SQLite ağ diskinde güvenli değildir). Çok kullanıcı için web sürümünü kullanın.
