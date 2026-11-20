using System.Data;
using System.Security.Cryptography;

namespace StokTakip.Core;

/// <summary>Dosya ekleri: yükleme klasörüne yıl/ay altında rastgele adla kopyalanır (web sürümüyle aynı düzen).</summary>
public static class Attachments
{
    public static long Save(DbSession db, AppUser u, string entity, long entityId, string sourceFile, string kind)
    {
        var ext = Path.GetExtension(sourceFile).TrimStart('.').ToLowerInvariant();
        if (!Perms.AllowedExt.Contains(ext)) throw new StockException($"'{Path.GetFileName(sourceFile)}' türü desteklenmiyor ({ext}).");
        var size = new FileInfo(sourceFile).Length;
        if (size > 40L * 1024 * 1024) throw new StockException("Dosya çok büyük (en fazla 40 MB).");
        var sub = DateTime.Now.ToString("yyyy/MM");
        var stored = $"{sub}/{Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()}.{ext}";
        var dest = FullPath(stored);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(sourceFile, dest);
        if (!Perms.AttachKinds.Contains(kind)) kind = "Diğer";
        var id = db.Insert("INSERT INTO attachments (entity, entity_id, kind, filename, stored_name, size, user_id, created_at) VALUES (?,?,?,?,?,?,?,?)",
            entity, entityId, kind, Path.GetFileName(sourceFile), stored, size, u.Id, Db.Now());
        if (entity == "product" && Perms.ImageExt.Contains(ext))
            db.Exec("UPDATE products SET image_id=COALESCE(image_id, ?) WHERE id=?", id, entityId);
        Audit.Log(db, u, "Dosya yüklendi", $"{entity}#{entityId}: {Path.GetFileName(sourceFile)} ({kind})");
        return id;
    }

    public static string FullPath(string stored) =>
        Path.GetFullPath(Path.Combine(Db.UploadDir, stored.Replace('/', Path.DirectorySeparatorChar)));

    public static DataTable List(DbSession db, string entity, long entityId) =>
        db.Query("SELECT a.id, a.kind AS Tür, a.filename AS Dosya, ROUND(a.size/1024.0) AS KB, substr(a.created_at,1,16) AS Tarih, " +
                 "u.full_name AS Yükleyen, a.stored_name, a.user_id, a.entity FROM attachments a LEFT JOIN users u ON u.id=a.user_id " +
                 "WHERE entity=? AND entity_id=? ORDER BY a.id DESC", entity, entityId);

    /// <summary>Fatura/teklif gibi fiyat içeren belgeler sadece yetkililere açık.</summary>
    public static bool CanView(AppUser u, string entity, string kind) =>
        !(entity is "movement" or "supplier" || kind is "Fatura" or "Teklif") || u.CanAny("can_see_price", "can_purchase", "can_in");

    public static void Delete(DbSession db, AppUser u, long id)
    {
        var a = db.Row("SELECT * FROM attachments WHERE id=?", id) ?? throw new StockException("Dosya bulunamadı.");
        if (!(u.Can("can_edit") || a.Lng("user_id") == u.Id)) throw new StockException("Bu dosyayı silme yetkiniz yok.");
        db.Exec("DELETE FROM attachments WHERE id=?", id);
        db.Exec("UPDATE products SET image_id=NULL WHERE image_id=?", id);
        Audit.Log(db, u, "Dosya silindi", $"{a.Str("entity")}#{a.Lng("entity_id")}: {a.Str("filename")}");
        try { File.Delete(FullPath(a.Str("stored_name"))); } catch { /* dosya zaten yoksa sorun değil */ }
    }
}
