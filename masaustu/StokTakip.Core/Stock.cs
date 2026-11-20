using System.Data;

namespace StokTakip.Core;

public class StockException : Exception
{
    public StockException(string m) : base(m) { }
}

public class MoveMeta
{
    public long? SupplierId;
    public double? UnitPrice;
    public string Currency = "TRY";
    public string DocumentNo = "", DocumentDate = "", Project = "", Receiver = "", Note = "";
    public long? RequestId;
}

/// <summary>Stok hareketlerinin tek kaynağı. Lokasyon stoğu, ürün toplamı ve hareket kaydı birlikte güncellenir.</summary>
public static class Stock
{
    /// <summary>Hareketi uygular. Çağıran Commit/Rollback yapar. İşlem yoksa yazma kilidiyle başlatır.</summary>
    public static (long MoveId, double After, DataRow Product) Apply(DbSession db, AppUser user, long pid, string kind,
        double qty, long? fromLoc, long? toLoc, MoveMeta? meta = null)
    {
        meta ??= new MoveMeta();
        db.Begin();
        var p = db.Row("SELECT * FROM products WHERE id=?", pid);
        if (p == null || !p.Bool("active")) throw new StockException("Ürün bulunamadı veya pasif.");

        double LocQty(long lid) => db.Scalar<double?>("SELECT qty FROM stock WHERE product_id=? AND location_id=?", pid, lid) ?? 0;
        void SetQty(long lid, double q) => db.Exec(
            "INSERT INTO stock (product_id, location_id, qty) VALUES (?,?,?) " +
            "ON CONFLICT(product_id, location_id) DO UPDATE SET qty=excluded.qty", pid, lid, Math.Round(q, 6));

        foreach (var lid in new[] { fromLoc, toLoc })
            if (lid != null && db.Scalar<long>("SELECT COUNT(*) FROM locations WHERE id=? AND active=1", lid) == 0)
                throw new StockException("Seçilen lokasyon bulunamadı veya pasif.");

        double delta;
        string code = p.Str("code"), unit = p.Str("unit");
        switch (kind)
        {
            case "GIRIS":
                if (toLoc == null) throw new StockException("Giriş lokasyonu seçin.");
                if (qty <= 0) throw new StockException("Miktar sıfırdan büyük olmalı.");
                SetQty(toLoc.Value, LocQty(toLoc.Value) + qty);
                delta = qty;
                break;
            case "CIKIS":
            {
                if (fromLoc == null) throw new StockException("Çıkış yapılacak lokasyonu seçin.");
                if (qty <= 0) throw new StockException("Miktar sıfırdan büyük olmalı.");
                var have = LocQty(fromLoc.Value);
                if (have + 1e-9 < qty)
                    throw new StockException($"{code}: Bu lokasyonda yetersiz stok! Mevcut {Fmt.Qty(have)} {unit}, istenen {Fmt.Qty(qty)}.");
                SetQty(fromLoc.Value, have - qty);
                delta = -qty;
                break;
            }
            case "TRANSFER":
            {
                if (fromLoc == null || toLoc == null || fromLoc == toLoc)
                    throw new StockException("Transfer için farklı kaynak ve hedef lokasyon seçin.");
                if (qty <= 0) throw new StockException("Miktar sıfırdan büyük olmalı.");
                var have = LocQty(fromLoc.Value);
                if (have + 1e-9 < qty) throw new StockException($"Kaynak lokasyonda yetersiz stok (mevcut {Fmt.Qty(have)}).");
                SetQty(fromLoc.Value, have - qty);
                SetQty(toLoc.Value, LocQty(toLoc.Value) + qty);
                delta = 0;
                break;
            }
            case "SAYIM":
                if (toLoc == null) throw new StockException("Sayım lokasyonu seçin.");
                if (qty < 0) throw new StockException("Sayım miktarı negatif olamaz.");
                delta = qty - LocQty(toLoc.Value);
                SetQty(toLoc.Value, qty);
                qty = Math.Round(delta, 6);
                break;
            default:
                throw new StockException("Geçersiz hareket tipi.");
        }

        var before = p.Dbl("quantity");
        var after = Math.Round(before + delta, 6);
        db.Exec("UPDATE products SET quantity=?, updated_at=? WHERE id=?", after, Db.Now(), pid);
        if (kind == "GIRIS" && meta.UnitPrice != null)
            db.Exec("UPDATE products SET last_price=?, currency=? WHERE id=?", meta.UnitPrice, meta.Currency, pid);
        if (kind == "GIRIS" && meta.SupplierId != null && p.NLng("default_supplier_id") == null)
            db.Exec("UPDATE products SET default_supplier_id=? WHERE id=?", meta.SupplierId, pid);
        if ((kind == "GIRIS" || kind == "SAYIM") && toLoc != null && p.NLng("default_location_id") == null)
            db.Exec("UPDATE products SET default_location_id=? WHERE id=?", toLoc, pid);

        var mid = db.Insert(
            "INSERT INTO movements (product_id, user_id, type, qty, from_location_id, to_location_id, qty_before, qty_after," +
            " supplier_id, unit_price, currency, document_no, document_date, project, receiver, request_id, note, created_at)" +
            " VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
            pid, user.Id, kind, qty,
            kind is "CIKIS" or "TRANSFER" ? fromLoc : null,
            kind != "CIKIS" ? toLoc : null,
            before, after, meta.SupplierId, meta.UnitPrice, meta.UnitPrice != null ? meta.Currency : null,
            meta.DocumentNo, meta.DocumentDate, meta.Project, meta.Receiver, meta.RequestId, meta.Note, Db.Now());
        return (mid, after, p);
    }

    public static DataRow? FindProduct(DbSession db, string codeOrBarcode) =>
        db.Row("SELECT * FROM products WHERE (code=? OR (barcode<>'' AND barcode=?)) AND active=1",
            codeOrBarcode.Trim(), codeOrBarcode.Trim());

    /// <summary>Ürünün lokasyonlardaki stoğu: (lokasyon id, yol, miktar).</summary>
    public static List<(long Id, string Path, double Qty)> Stocks(DbSession db, long pid, bool onlyPositive = false)
    {
        var paths = Tree.Paths(db, "locations");
        return db.Query("SELECT location_id, qty FROM stock WHERE product_id=? AND qty" + (onlyPositive ? ">0" : "<>0"), pid)
            .Rows.Cast<DataRow>()
            .Select(r => (r.Lng("location_id"), paths.GetValueOrDefault(r.Lng("location_id"), "?"), r.Dbl("qty")))
            .OrderBy(x => x.Item2).ToList();
    }

    public const string MoveSelect =
        "SELECT m.*, p.code, p.name, p.unit, u.full_name, s.name AS supplier_name FROM movements m " +
        "JOIN products p ON p.id=m.product_id JOIN users u ON u.id=m.user_id LEFT JOIN suppliers s ON s.id=m.supplier_id";
}
