using System.Data;

namespace StokTakip.Core;

/// <summary>Lokasyon ve kategori ağaçları için yol ("Ana Ambar / A Bölgesi / Raf 3") hesaplama.</summary>
public static class Tree
{
    public static Dictionary<long, string> Paths(DbSession db, string table)
    {
        var rows = db.Query($"SELECT id, parent_id, name FROM {table}").Rows.Cast<DataRow>()
            .ToDictionary(r => r.Lng("id"));
        var cache = new Dictionary<long, string>();
        string Path(long id, int depth)
        {
            if (cache.TryGetValue(id, out var p)) return p;
            if (!rows.TryGetValue(id, out var r) || depth > 20) return "";
            var parent = r.NLng("parent_id");
            p = parent is null ? r.Str("name") : $"{Path(parent.Value, depth + 1)} / {r.Str("name")}";
            cache[id] = p;
            return p;
        }
        return rows.Keys.ToDictionary(k => k, k => Path(k, 0));
    }

    public static List<long> SubtreeIds(DbSession db, string table, long root) =>
        db.Query($"WITH RECURSIVE t(id) AS (SELECT ? UNION ALL SELECT c.id FROM {table} c JOIN t ON c.parent_id=t.id) SELECT id FROM t", root)
          .Rows.Cast<DataRow>().Select(r => r.Lng("id")).ToList();

    /// <summary>Açılır listeler için (id, "yol [KOD]") sıralı liste.</summary>
    public static List<KeyValuePair<long, string>> LocationOptions(DbSession db, bool onlyActive = true)
    {
        var paths = Paths(db, "locations");
        return db.Query("SELECT id, code, active FROM locations").Rows.Cast<DataRow>()
            .Where(r => !onlyActive || r.Bool("active"))
            .Select(r => new KeyValuePair<long, string>(r.Lng("id"), $"{paths[r.Lng("id")]}  [{r.Str("code")}]"))
            .OrderBy(x => x.Value, StringComparer.Create(Fmt.Tr, true)).ToList();
    }

    public static List<KeyValuePair<long, string>> CategoryOptions(DbSession db) =>
        Paths(db, "categories").OrderBy(x => x.Value, StringComparer.Create(Fmt.Tr, true)).ToList();
}
