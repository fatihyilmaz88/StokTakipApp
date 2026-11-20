using System.Data;
using System.Text;

namespace StokTakip.Core;

public class ProductData
{
    public long? Id;
    public string Code = "", Name = "", Description = "", Brand = "", Model = "", Barcode = "", TechSpec = "", Unit = "adet", Currency = "TRY";
    public double MinStock, MaxStock;
    public double? LastPrice;            // null: fiyat görme yetkisi yok, değiştirme
    public long? CategoryId, DefaultLocationId, DefaultSupplierId;
    public bool Active = true;

    public static ProductData From(DataRow r) => new()
    {
        Id = r.Lng("id"), Code = r.Str("code"), Name = r.Str("name"), Description = r.Str("description"), Brand = r.Str("brand"),
        Model = r.Str("model"), Barcode = r.Str("barcode"), TechSpec = r.Str("tech_spec"), Unit = r.Str("unit"),
        Currency = r.Str("currency"), MinStock = r.Dbl("min_stock"), MaxStock = r.Dbl("max_stock"), LastPrice = r.Dbl("last_price"),
        CategoryId = r.NLng("category_id"), DefaultLocationId = r.NLng("default_location_id"),
        DefaultSupplierId = r.NLng("default_supplier_id"), Active = r.Bool("active"),
    };
}

public static class Products
{
    private static readonly string[] Cols =
        { "code", "name", "description", "brand", "model", "barcode", "tech_spec", "unit", "currency", "min_stock", "max_stock",
          "category_id", "default_location_id", "default_supplier_id", "active" };

    private static object?[] Vals(ProductData d) =>
        new object?[] { d.Code.Trim(), d.Name.Trim(), d.Description.Trim(), d.Brand.Trim(), d.Model.Trim(), d.Barcode.Trim(),
            d.TechSpec.Trim(), string.IsNullOrWhiteSpace(d.Unit) ? "adet" : d.Unit.Trim(),
            Perms.Currencies.Contains(d.Currency) ? d.Currency : "TRY", d.MinStock, d.MaxStock, d.CategoryId,
            d.DefaultLocationId, d.DefaultSupplierId, d.Active ? 1 : 0 };

    public static void Validate(ProductData d)
    {
        if (string.IsNullOrWhiteSpace(d.Code)) throw new StockException("Stok kodu zorunlu.");
        if (string.IsNullOrWhiteSpace(d.Name)) throw new StockException("Ürün adı zorunlu.");
        if (d.MinStock < 0 || d.MaxStock < 0) throw new StockException("Min./maks. stok negatif olamaz.");
        if (d.LastPrice < 0) throw new StockException("Fiyat negatif olamaz.");
    }

    /// <summary>Yeni ürün ekler veya günceller; değişiklikleri denetim kaydına yazar. Ürün id döner.</summary>
    public static long Save(DbSession db, AppUser u, ProductData d, double initialQty = 0)
    {
        Validate(d);
        var cols = Cols.ToList();
        var vals = Vals(d).ToList();
        if (d.LastPrice != null && u.Can("can_see_price")) { cols.Add("last_price"); vals.Add(d.LastPrice); }
        db.Begin();
        if (db.Scalar<long>("SELECT COUNT(*) FROM products WHERE code=? AND id<>?", d.Code.Trim(), d.Id ?? -1) > 0)
            throw new StockException("Bu stok kodu zaten kayıtlı.");
        if (d.Id == null)
        {
            if (initialQty < 0) throw new StockException("Başlangıç miktarı negatif olamaz.");
            if (initialQty > 0 && d.DefaultLocationId == null) throw new StockException("Başlangıç miktarı için varsayılan lokasyon seçin.");
            var id = db.Insert($"INSERT INTO products ({string.Join(",", cols)}, created_by, created_at, updated_at) " +
                               $"VALUES ({string.Join(",", cols.Select(_ => "?"))},?,?,?)",
                               vals.Concat(new object?[] { u.Id, Db.Now(), Db.Now() }).ToArray());
            if (initialQty > 0) Stock.Apply(db, u, id, "SAYIM", initialQty, null, d.DefaultLocationId, new MoveMeta { Note = "Açılış stoğu" });
            Audit.Log(db, u, "Ürün oluşturuldu", $"{d.Code} - {d.Name}");
            return id;
        }
        var old = db.Row("SELECT * FROM products WHERE id=?", d.Id) ?? throw new StockException("Ürün bulunamadı.");
        var changes = new List<string>();
        for (int i = 0; i < cols.Count; i++)
        {
            var o = old[cols[i]] is DBNull ? "" : Convert.ToString(old[cols[i]], Fmt.Tr);
            var n = vals[i] == null ? "" : Convert.ToString(vals[i], Fmt.Tr);
            if (o != n) changes.Add($"{cols[i]}: '{o}' → '{n}'");
        }
        db.Exec($"UPDATE products SET {string.Join(", ", cols.Select(c => c + "=?"))}, updated_at=? WHERE id=?",
            vals.Concat(new object?[] { Db.Now(), d.Id }).ToArray());
        if (changes.Count > 0) Audit.Log(db, u, "Ürün güncellendi", $"{old.Str("code")}: " + string.Join("; ", changes));
        return d.Id.Value;
    }

    /// <summary>CSV ile toplu ürün: kod;ad;kategori;birim;lokasyon_kodu;min_stok;miktar;marka;model;tedarikci;fiyat;para</summary>
    public static (int Added, int Skipped, List<string> Errors) ImportCsv(DbSession db, AppUser u, string file)
    {
        var text = File.ReadAllText(file, Encoding.UTF8);
        var delim = text.Count(c => c == ';') >= text.Count(c => c == ',') ? ';' : ',';
        int added = 0, skipped = 0, line = 0;
        var errors = new List<string>();
        db.Begin();
        foreach (var raw in Csv.Parse(text, delim))
        {
            line++;
            if (raw.Count == 0 || string.IsNullOrWhiteSpace(raw[0])) continue;
            if (line == 1 && raw[0].Trim().ToLowerInvariant() is "kod" or "stok kodu" or "code") continue;
            var c = raw.Select(x => x.Trim()).Concat(Enumerable.Repeat("", 12)).Take(12).ToArray();
            var (code, name, cat, unit, loc, mn, qty, brand, model, sup, price, cur) = (c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10], c[11]);
            double? mnV = Fmt.ParseNum(mn, 0), qtyV = Fmt.ParseNum(qty, 0), prV = Fmt.ParseNum(price, 0);
            if (name == "" || mnV == null || qtyV == null || qtyV < 0 || prV == null) { errors.Add($"Satır {line}: hatalı veri"); continue; }
            if (db.Scalar<long>("SELECT COUNT(*) FROM products WHERE code=?", code) > 0) { skipped++; continue; }
            long? catId = null;
            foreach (var part in cat.Split('/').Select(x => x.Trim()).Where(x => x != ""))
                catId = db.Scalar<long?>("SELECT id FROM categories WHERE name=? AND parent_id IS ?", part, catId)
                        ?? db.Insert("INSERT INTO categories (name, parent_id) VALUES (?,?)", part, catId);
            var root = db.Scalar<long>("SELECT id FROM locations WHERE parent_id IS NULL ORDER BY id LIMIT 1");
            long? locId = null;
            if (loc != "")
                locId = db.Scalar<long?>("SELECT id FROM locations WHERE code=?", loc)
                        ?? db.Insert("INSERT INTO locations (code, name, type, parent_id) VALUES (?,?,'Raf',?)", loc.ToUpperInvariant(), loc, root);
            long? supId = null;
            if (sup != "")
                supId = db.Scalar<long?>("SELECT id FROM suppliers WHERE name=?", sup)
                        ?? db.Insert("INSERT INTO suppliers (name, created_at) VALUES (?,?)", sup, Db.Now());
            if (qtyV > 0 && locId == null) locId = root;
            var pid = db.Insert("INSERT INTO products (code, name, category_id, unit, default_location_id, min_stock, brand, model, " +
                                "default_supplier_id, last_price, currency, created_by, created_at, updated_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                code, name, catId, unit == "" ? "adet" : unit, locId, mnV, brand, model, supId, prV,
                Perms.Currencies.Contains(cur.ToUpperInvariant()) ? cur.ToUpperInvariant() : "TRY", u.Id, Db.Now(), Db.Now());
            if (qtyV > 0) Stock.Apply(db, u, pid, "SAYIM", qtyV.Value, null, locId, new MoveMeta { Note = "Toplu içe aktarma" });
            added++;
        }
        Audit.Log(db, u, "Toplu ürün içe aktarma", $"{added} eklendi, {skipped} atlandı");
        return (added, skipped, errors);
    }
}

public static class Csv
{
    /// <summary>Excel uyumlu CSV yazar (UTF-8 BOM, ";" ayırıcı).</summary>
    public static void Write(string file, DataTable t, IEnumerable<string>? skipColumns = null)
    {
        var skip = new HashSet<string>(skipColumns ?? Array.Empty<string>());
        var cols = t.Columns.Cast<DataColumn>().Where(c => !skip.Contains(c.ColumnName)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(";", cols.Select(c => Esc(c.ColumnName))));
        foreach (DataRow r in t.Rows)
            sb.AppendLine(string.Join(";", cols.Select(c => Esc(r[c] switch
            {
                DBNull => "",
                double d => Fmt.Xl(d),
                _ => Convert.ToString(r[c], Fmt.Tr) ?? "",
            }))));
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Esc(string s) =>
        s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static IEnumerable<List<string>> Parse(string text, char delim)
    {
        var row = new List<string>();
        var cell = new StringBuilder();
        bool q = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (q)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') q = false;
                else cell.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == delim) { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n')
            {
                row.Add(cell.ToString().TrimEnd('\r')); cell.Clear();
                yield return row; row = new List<string>();
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString().TrimEnd('\r')); yield return row; }
    }
}
