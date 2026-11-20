using System.Data;

namespace StokTakip.Core;

/// <summary>Malzeme Talebi → Onay → Ambardan Teslim iş akışı.</summary>
public static class Requests
{
    public static long Create(DbSession db, AppUser u, string project, string neededDate, string priority, string note,
        IEnumerable<(long ProductId, double Qty, string Note)> items)
    {
        var list = items.ToList();
        if (string.IsNullOrWhiteSpace(project)) throw new StockException("Proje / İş emri / Makine alanı zorunlu.");
        if (list.Count == 0) throw new StockException("En az bir ürün ekleyin.");
        if (list.Any(i => i.Qty <= 0)) throw new StockException("Miktarlar sıfırdan büyük olmalı.");
        db.Begin();
        var rid = db.Insert("INSERT INTO requests (requester_id, department, project, needed_date, priority, note, created_at, updated_at)" +
                            " VALUES (?,?,?,?,?,?,?,?)", u.Id, u.Department, project.Trim(), neededDate, priority, note.Trim(), Db.Now(), Db.Now());
        foreach (var it in list)
            db.Exec("INSERT INTO request_items (request_id, product_id, qty, note) VALUES (?,?,?,?)", rid, it.ProductId, it.Qty, it.Note);
        Audit.Log(db, u, "Malzeme talebi oluşturuldu", Perms.ReqNo(rid, Db.Now()));
        return rid;
    }

    public static DataRow Get(DbSession db, long rid) =>
        db.Row("SELECT r.*, u.full_name, a.full_name AS approver FROM requests r JOIN users u ON u.id=r.requester_id " +
               "LEFT JOIN users a ON a.id=r.approver_id WHERE r.id=?", rid) ?? throw new StockException("Talep bulunamadı.");

    public static DataTable Items(DbSession db, long rid) =>
        db.Query("SELECT ri.*, p.code, p.name, p.unit, p.quantity, p.brand, p.model FROM request_items ri " +
                 "JOIN products p ON p.id=ri.product_id WHERE ri.request_id=?", rid);

    public static bool CanView(AppUser u, DataRow r) =>
        r.Lng("requester_id") == u.Id || u.CanAny("can_approve", "can_out", "can_report");

    public static void Decide(DbSession db, AppUser u, long rid, bool approve, string note)
    {
        if (!u.Can("can_approve")) throw new StockException("Onay yetkiniz yok.");
        var r = Get(db, rid);
        if (r.Str("status") != "BEKLIYOR") throw new StockException("Bu talep artık onay beklemiyor.");
        if (!approve && string.IsNullOrWhiteSpace(note)) throw new StockException("Red gerekçesi yazın.");
        var st = approve ? "ONAYLANDI" : "REDDEDILDI";
        db.Exec("UPDATE requests SET status=?, approver_id=?, approved_at=?, decision_note=?, updated_at=? WHERE id=?",
            st, u.Id, Db.Now(), note.Trim(), Db.Now(), rid);
        Audit.Log(db, u, "Talep " + Perms.ReqStatus[st], Perms.ReqNo(rid, r.Str("created_at")));
    }

    public static void Cancel(DbSession db, AppUser u, long rid)
    {
        var r = Get(db, rid);
        if (r.Lng("requester_id") != u.Id && !u.IsAdmin) throw new StockException("Sadece talep sahibi iptal edebilir.");
        if (r.Str("status") is not ("BEKLIYOR" or "ONAYLANDI")) throw new StockException("Bu talep iptal edilemez.");
        db.Exec("UPDATE requests SET status='IPTAL', updated_at=? WHERE id=?", Db.Now(), rid);
        Audit.Log(db, u, "Talep iptal edildi", Perms.ReqNo(rid, r.Str("created_at")));
    }

    /// <summary>Ambar teslimi. issues: (request_item id, miktar, lokasyon). Hepsi tek işlemde; hata olursa hiçbiri yazılmaz.</summary>
    public static string Issue(DbSession db, AppUser u, long rid, IEnumerable<(long ItemId, double Qty, long? LocId)> issues, string receiver)
    {
        if (!u.Can("can_out")) throw new StockException("Stok çıkış yetkiniz yok.");
        var r = Get(db, rid);
        if (r.Str("status") is not ("ONAYLANDI" or "KISMI")) throw new StockException("Sadece onaylanmış talepler karşılanabilir.");
        var items = Items(db, rid).Rows.Cast<DataRow>().ToDictionary(x => x.Lng("id"));
        var no = Perms.ReqNo(rid, r.Str("created_at"));
        db.Begin();
        int done = 0;
        foreach (var (itemId, qty, loc) in issues)
        {
            if (qty <= 0) continue;
            if (!items.TryGetValue(itemId, out var it)) throw new StockException("Geçersiz kalem.");
            var remaining = it.Dbl("qty") - it.Dbl("issued_qty");
            if (qty > remaining + 1e-9)
                throw new StockException($"{it.Str("code")}: kalan talep miktarından ({Fmt.Qty(remaining)}) fazla teslim edilemez.");
            Stock.Apply(db, u, it.Lng("product_id"), "CIKIS", qty, loc, null, new MoveMeta
            {
                Project = r.Str("project"), Receiver = string.IsNullOrWhiteSpace(receiver) ? r.Str("full_name") : receiver.Trim(),
                RequestId = rid, Note = "Talep " + no,
            });
            db.Exec("UPDATE request_items SET issued_qty=issued_qty+? WHERE id=?", qty, itemId);
            done++;
        }
        if (done == 0) throw new StockException("Teslim edilecek miktar girilmedi.");
        var left = db.Scalar<long>("SELECT COUNT(*) FROM request_items WHERE request_id=? AND issued_qty + 1e-9 < qty", rid);
        var st = left > 0 ? "KISMI" : "TAMAMLANDI";
        db.Exec("UPDATE requests SET status=?, updated_at=? WHERE id=?", st, Db.Now(), rid);
        Audit.Log(db, u, "Talep karşılandı", $"{no} → {Perms.ReqStatus[st]}");
        return st;
    }

    public static void Close(DbSession db, AppUser u, long rid)
    {
        if (!u.CanAny("can_out", "can_approve")) throw new StockException("Yetkiniz yok.");
        var r = Get(db, rid);
        if (r.Str("status") != "KISMI") return;
        db.Exec("UPDATE requests SET status='TAMAMLANDI', updated_at=? WHERE id=?", Db.Now(), rid);
        Audit.Log(db, u, "Talep kapatıldı (kısmi)", Perms.ReqNo(rid, r.Str("created_at")));
    }
}
