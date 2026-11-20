# StokTakipApp – Fabrika Stok & Ambar Yönetimi

Fabrika ambarı için stok takip programı. İki sürüm var, **aynı veritabanı yapısını** kullanırlar:

| Klasör | Sürüm | Ne zaman |
|---|---|---|
| [`web/`](web/) | Sunucu + tarayıcı (Python/Flask) | Birden fazla bilgisayar ve telefondan erişim |
| [`masaustu/`](masaustu/) | Windows masaüstü (C# / .NET 8 WinForms, Visual Studio 2022) | Tek bilgisayarda çalıştırma ve geliştirme |

## Özellikler
- Ürün kartı: marka, model/parça no, barkod, teknik özellik, kategori ağacı, tedarikçi, fiyat, dosyalar
- Depo › Bölge › Raf › Göz lokasyon ağacı, lokasyon bazlı stok
- Giriş (fatura/irsaliye, fiyat, fatura dosyası), Çıkış (proje/makine, teslim alan), Transfer, Sayım
- Malzeme Talebi → Onay → Ambardan teslim iş akışı
- Rol şablonlu kullanıcı yetkileri (Müdür, Ambar, Satın Alma, Üretim, Tasarım, Bakım…)
- Raporlar (tüketim, sipariş önerisi, stok değeri, hareketsiz stok, denetim kaydı), Excel (CSV) aktarım
- Web sürümünde QR etiket ve telefondan kullanım

Kurulum: [web/KURULUM.md](web/KURULUM.md) · [masaustu/README.md](masaustu/README.md)
