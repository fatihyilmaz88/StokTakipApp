using System.Data;

namespace StokTakip.Core;

/// <summary>Raporlar. Sonuçlar Türkçe kolon adlı DataTable olarak döner; doğrudan tabloya bağlanır veya CSV'ye yazılır.</summary>
public static class Reports
{
    public static DataTable Consumption(DbSession db, AppUser u, string from, string to, string group)
    {
        bool price = u.Can("can_see_price");
        string val = price ? ", ROUND(SUM(m.qty * p.last_price),2) AS \"Tahmini Tutar\", p.currency AS \"Para\"" : "";
        if (group == "urun")
            return db.Query($"SELECT p.code AS \"Stok Kodu\", p.name AS \"Ürün\", p.unit AS \"Birim\", SUM(m.qty) AS \"Çıkan Miktar\", COUNT(*) AS \"Hareket\"{val} " +
                            "FROM movements m JOIN products p ON p.id=m.product_id WHERE m.type='CIKIS' AND m.created_at>=? " +
                            "AND m.created_at<date(?,'+1 day') GROUP BY p.id ORDER BY 4 DESC", from, to);
        var (col, title) = group switch
        {
            "kullanici" => ("u.full_name", "Çıkışı Yapan"),
            "departman" => ("u.department", "Departman"),
            "teslim" => ("m.receiver", "Teslim Alan"),
            _ => ("m.project", "Proje / Makine"),
        };
        return db.Query($"SELECT COALESCE(NULLIF({col},''),'(boş)') AS \"{title}\", p.code AS \"Stok Kodu\", p.name AS \"Ürün\", p.unit AS \"Birim\", " +
                        $"SUM(m.qty) AS \"Miktar\"{val} FROM movements m JOIN products p ON p.id=m.product_id JOIN users u ON u.id=m.user_id " +
                        $"WHERE m.type='CIKIS' AND m.created_at>=? AND m.created_at<date(?,'+1 day') GROUP BY {col}, p.id ORDER BY 1, 5 DESC", from, to);
    }

    public static DataTable Reorder(DbSession db, AppUser u)
    {
        bool price = u.Can("can_see_price");
        var t = db.Query(
            "SELECT COALESCE(s.name,'(tedarikçi yok)') AS \"Tedarikçi\", p.code AS \"Kod\", p.name AS \"Ürün\", trim(p.brand||' '||p.model) AS \"Marka/Model\", " +
            "p.unit AS \"Birim\", p.quantity AS \"Mevcut\", p.min_stock AS \"Min.\", " +
            "(SELECT COALESCE(SUM(ri.qty-ri.issued_qty),0) FROM request_items ri JOIN requests r ON r.id=ri.request_id WHERE ri.product_id=p.id " +
            " AND r.status IN ('BEKLIYOR','ONAYLANDI','KISMI')) AS \"Açık Talep\", " +
            "MAX(CASE WHEN p.max_stock>p.min_stock THEN p.max_stock ELSE p.min_stock*2 END - p.quantity + " +
            " (SELECT COALESCE(SUM(ri.qty-ri.issued_qty),0) FROM request_items ri JOIN requests r ON r.id=ri.request_id WHERE ri.product_id=p.id " +
            " AND r.status IN ('BEKLIYOR','ONAYLANDI','KISMI')), 0) AS \"Önerilen Sipariş\", p.last_price AS \"Son Fiyat\", p.currency AS \"Para\" " +
            "FROM products p LEFT JOIN suppliers s ON s.id=p.default_supplier_id " +
            "WHERE p.active=1 AND p.min_stock>0 AND p.quantity<=p.min_stock ORDER BY 1, 2");
        if (!price) { t.Columns.Remove("Son Fiyat"); t.Columns.Remove("Para"); }
        else
        {
            t.Columns.Add("Tutar", typeof(object));
            foreach (DataRow r in t.Rows) r["Tutar"] = Math.Round(r.Dbl("Önerilen Sipariş") * r.Dbl("Son Fiyat"), 2);
        }
        return t;
    }

    public static DataTable Value(DbSession db, string group)
    {
        DataTable src;
        Dictionary<long, string> paths;
        if (group == "lokasyon")
        {
            paths = Tree.Paths(db, "locations");
            src = db.Query("SELECT s.location_id AS k, p.currency, SUM(s.qty*p.last_price) v, COUNT(*) n FROM stock s " +
                           "JOIN products p ON p.id=s.product_id WHERE s.qty>0 GROUP BY s.location_id, p.currency");
        }
        else
        {
            paths = Tree.Paths(db, "categories");
            src = db.Query("SELECT category_id AS k, currency, SUM(quantity*last_price) v, COUNT(*) n FROM products " +
                           "WHERE active=1 AND quantity>0 GROUP BY category_id, currency");
        }
        var t = new DataTable();
        t.Columns.Add(group == "lokasyon" ? "Lokasyon" : "Kategori", typeof(object));
        t.Columns.Add("Kalem", typeof(object));
        t.Columns.Add("Para", typeof(object));
        t.Columns.Add("Değer", typeof(object));
        var totals = new Dictionary<string, double>();
        foreach (var r in src.Rows.Cast<DataRow>().OrderBy(r => r.NLng("k") is long k ? paths.GetValueOrDefault(k, "") : ""))
        {
            var name = r.NLng("k") is long k ? paths.GetValueOrDefault(k, "?") : "(kategorisiz)";
            t.Rows.Add(name, r.Lng("n"), r.Str("currency"), Math.Round(r.Dbl("v"), 2));
            totals[r.Str("currency")] = totals.GetValueOrDefault(r.Str("currency")) + r.Dbl("v");
        }
        foreach (var (cur, v) in totals) t.Rows.Add("TOPLAM", DBNull.Value, cur, Math.Round(v, 2));
        return t;
    }

    public static DataTable Dead(DbSession db, AppUser u, int days)
    {
        var t = db.Query(
            "SELECT p.code AS \"Kod\", p.name AS \"Ürün\", p.quantity AS \"Miktar\", p.unit AS \"Birim\", " +
            "substr(COALESCE((SELECT MAX(created_at) FROM movements WHERE product_id=p.id AND type IN ('CIKIS','GIRIS')),'-'),1,10) AS \"Son Hareket\", " +
            "ROUND(p.quantity*p.last_price,2) AS \"Bağlı Değer\", p.currency AS \"Para\" FROM products p WHERE p.active=1 AND p.quantity>0 " +
            "AND COALESCE((SELECT MAX(created_at) FROM movements WHERE product_id=p.id AND type IN ('CIKIS','GIRIS')), p.created_at) " +
            "< datetime('now','localtime', ?) ORDER BY 5", $"-{days} day");
        if (!u.Can("can_see_price")) { t.Columns.Remove("Bağlı Değer"); t.Columns.Remove("Para"); }
        return t;
    }

    public static DataTable AuditLog(DbSession db) =>
        db.Query("SELECT a.created_at AS \"Zaman\", COALESCE(u.full_name,'-') AS \"Kullanıcı\", a.action AS \"İşlem\", a.detail AS \"Detay\", " +
                 "a.ip AS \"Bilgisayar/IP\" FROM audit a LEFT JOIN users u ON u.id=a.user_id ORDER BY a.id DESC LIMIT 2000");
}
