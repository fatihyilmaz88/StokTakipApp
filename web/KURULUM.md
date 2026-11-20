# Fabrika Stok & Ambar Yönetimi (v2) – Kurulum ve Kullanım

## 1. Sunucuya kurulum (Windows)
1. Sunucuya **Python 3.10+** kurun (python.org → "Add Python to PATH" işaretli).
2. Klasörü sunucuya kopyalayın (ör. `C:\stok-takip`).
3. `baslat.bat` → ilk çalıştırmada paketler (flask, waitress, segno) kurulur.
4. Güvenlik duvarı: `netsh advfirewall firewall add rule name="Stok Takip" dir=in action=allow protocol=TCP localport=8000`
5. Sunucuya **sabit IP** verin (ör. 192.168.1.10). Otomatik başlatma: Görev Zamanlayıcı → "Bilgisayar başladığında" → `baslat.bat`.
6. Firma adını ekranda göstermek için `baslat.bat` içine `set STOK_FIRMA=Firma Adı` satırı ekleyin.

Bağlantı: **http://192.168.1.10:8000** — İlk giriş **admin / admin123** (ilk girişte şifre değişir).
Telefonda: Chrome/Safari → "Ana ekrana ekle".

## 2. İlk kurulum sırası (önerilen)
1. **Lokasyonlar**: Depo › Bölge › Raf › Göz ağacını kurun ("Seri raf/göz oluştur" ile R01…R20 tek seferde).
2. **Kategoriler**: Hidrolik › Valf, Elektrik › Kontaktör vb.
3. **Tedarikçiler**.
4. **Ürünler**: Excel → CSV ile toplu yükleyin (Ürünler › CSV İçe Aktar). Kategori/lokasyon/tedarikçi yoksa otomatik oluşur.
5. **QR etiketleri** yazdırıp raflara ve ürünlere yapıştırın.
6. **Kullanıcıları** rol şablonu seçerek açın.

## 3. Roller (şablon) ve yetkiler
| Rol | Yapabildikleri |
|---|---|
| Fabrika Müdürü | Talep onayı, fiyat/stok değeri, tüm raporlar |
| Ambar Sorumlusu | Giriş, çıkış, transfer, sayım, ürün/lokasyon tanımlama, talep karşılama |
| Ambar Personeli | Giriş, çıkış, transfer |
| Satın Alma | Tedarikçiler, mal kabul, fiyat ve fatura görme, sipariş önerisi |
| Üretim Şefi | Talep açma ve onaylama, raporlar |
| Üretim Personeli / Bakım | Sadece malzeme talebi açar, kendi taleplerini izler |
| Tasarım / Mühendislik | Ürün kartı tanımlama (marka, model, teknik özellik, datasheet), talep |
| Muhasebe | Fiyat, fatura, stok değeri |
| Sadece İzleme | Stokları ve ürün kartlarını görür |
Şablon seçtikten sonra kutuları kişiye özel değiştirebilirsiniz. **Fiyat ve faturalar sadece "Fiyat görme" yetkisi olanlara görünür.**

## 4. İş akışı: Malzeme Talebi
Üretim/Tasarım/Bakım talep açar → Şef/Müdür onaylar veya gerekçeyle reddeder → Ambar, hangi raftan verdiğini seçerek teslim eder (kısmi teslim olabilir) → stoktan otomatik düşer, talep "Teslim Edildi" olur. Her adım kim/ne zaman bilgisiyle saklanır.

## 5. QR / Barkod
- Sistemin bastığı QR etiketler sayfa adresi içerir: **telefonun normal kamerasıyla okutunca** ilgili ürün/raf sayfası açılır.
- USB/Bluetooth barkod okuyucu tüm kod alanlarında çalışır (ürünün üretici barkodunu "Barkod" alanına girebilirsiniz).
- Uygulama içi 📷 kamera okuma, Android Chrome'da ve HTTPS bağlantıda çalışır (http'de gizlenir).

## 6. Dosyalar
Ürüne: fotoğraf, datasheet, teknik resim (DWG/DXF/STEP/EPLAN), sertifika. Girişe: fatura/irsaliye PDF veya fotoğrafı.
Tedarikçiye: teklif, sözleşme. Talebe: teknik resim, liste. Dosyalar `uploads\` klasöründe durur (en fazla 40 MB/dosya).

## 7. Yedekleme (ÖNEMLİ)
`yedekle.bat` dosyasını Görev Zamanlayıcı ile **her gece** çalıştırın: veritabanını ve `uploads` klasörünü `yedek\` altına tarihli kopyalar.
Yedek klasörünü ayrıca başka bir diske / NAS'a alın.

## 8. Fabrika dışından erişim
Portu internete doğrudan **açmayın**. VPN (firma VPN'i, Tailscale, ZeroTier) kullanın.

## 9. v1'den geçiş
v2 veritabanı yapısı farklıdır. Eski `stok.db` dosyasını yedekleyip kaldırın; eski "Excel'e Aktar" listesini yeni CSV formatına göre düzenleyip içe aktarın.
