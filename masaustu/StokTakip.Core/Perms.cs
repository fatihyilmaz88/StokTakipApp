namespace StokTakip.Core;

/// <summary>Yetkiler, rol şablonları ve sabit listeler (web sürümüyle aynı).</summary>
public static class Perms
{
    public static readonly (string Key, string Label)[] All =
    {
        ("can_in", "Stok Girişi (mal kabul)"),
        ("can_out", "Stok Çıkışı / Talep Karşılama"),
        ("can_transfer", "Lokasyonlar Arası Transfer"),
        ("can_count", "Sayım Düzeltme"),
        ("can_edit", "Ürün / Kategori / Lokasyon Tanımlama"),
        ("can_request", "Malzeme Talebi Oluşturma"),
        ("can_approve", "Malzeme Talebi Onaylama"),
        ("can_purchase", "Tedarikçi Yönetimi"),
        ("can_see_price", "Fiyat, Fatura ve Stok Değeri Görme"),
        ("can_report", "Raporlar ve Excel'e Aktarma"),
        ("is_admin", "Sistem Yöneticisi (her şey + kullanıcı yönetimi)"),
    };

    public static readonly string[] Keys = All.Select(p => p.Key).ToArray();

    public static readonly Dictionary<string, string[]> Roles = new()
    {
        ["Fabrika Müdürü"] = new[] { "can_request", "can_approve", "can_see_price", "can_report" },
        ["Ambar Sorumlusu"] = new[] { "can_in", "can_out", "can_transfer", "can_count", "can_edit", "can_request", "can_report" },
        ["Ambar Personeli"] = new[] { "can_in", "can_out", "can_transfer" },
        ["Satın Alma"] = new[] { "can_in", "can_edit", "can_request", "can_purchase", "can_see_price", "can_report" },
        ["Üretim Şefi"] = new[] { "can_request", "can_approve", "can_report" },
        ["Üretim Personeli"] = new[] { "can_request" },
        ["Tasarım / Mühendislik"] = new[] { "can_edit", "can_request", "can_report" },
        ["Bakım / Elektrik"] = new[] { "can_request" },
        ["Kalite Kontrol"] = new[] { "can_report" },
        ["Muhasebe"] = new[] { "can_see_price", "can_report" },
        ["Sadece İzleme"] = Array.Empty<string>(),
        ["Sistem Yöneticisi"] = Keys,
    };

    public static readonly Dictionary<string, string> ReqStatus = new()
    {
        ["BEKLIYOR"] = "Onay Bekliyor",
        ["ONAYLANDI"] = "Onaylandı – Ambarda",
        ["KISMI"] = "Kısmen Teslim",
        ["TAMAMLANDI"] = "Teslim Edildi",
        ["REDDEDILDI"] = "Reddedildi",
        ["IPTAL"] = "İptal",
    };

    public static readonly Dictionary<string, string> MoveLabel = new()
    {
        ["GIRIS"] = "Giriş", ["CIKIS"] = "Çıkış", ["TRANSFER"] = "Transfer", ["SAYIM"] = "Sayım",
    };

    public static readonly string[] Currencies = { "TRY", "EUR", "USD" };
    public static readonly string[] LocTypes = { "Depo", "Bölge", "Koridor", "Raf", "Göz", "Saha", "Makine Yanı", "Diğer" };
    public static readonly string[] AttachKinds = { "Fatura", "İrsaliye", "Teklif", "Datasheet", "Teknik Resim", "Fotoğraf", "Sertifika", "Kullanım Kılavuzu", "Diğer" };
    public static readonly string[] Units = { "adet", "kg", "lt", "m", "m²", "paket", "koli", "takım", "rulo", "çift", "ton" };

    public static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf","jpg","jpeg","png","webp","gif","xlsx","xls","docx","doc","csv","txt","dwg","dxf",
        "step","stp","igs","iges","zip","7z","rar","eplan","zw1","edz",
    };
    public static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "webp", "gif" };

    public static string ReqNo(long id, string createdAt) =>
        $"MT-{(createdAt.Length >= 4 ? createdAt[..4] : DateTime.Now.Year.ToString())}-{id:00000}";
}
