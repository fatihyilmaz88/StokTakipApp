using System.Data;
using StokTakip.Core;

namespace StokTakip.Masaustu.Forms;

public class ProductsPage : UserControl, IPage
{
    private readonly DataGridView _grid = Ui.Grid();
    private readonly TextBox _q = new() { Width = 230, PlaceholderText = "Kod, ad, marka, model, barkod…" };
    private ComboBox _cat = new(), _loc = new(), _brand = new(), _sup = new();
    private readonly CheckBox _low = new() { Text = "Sadece kritik", AutoSize = true, Margin = new Padding(6, 8, 6, 0) };
    private readonly CheckBox _passive = new() { Text = "Pasifler", AutoSize = true, Margin = new Padding(6, 8, 6, 0) };
    private readonly Label _count = new() { AutoSize = true, Margin = new Padding(10, 9, 0, 0), ForeColor = Color.DimGray };
    private DataTable _data = new();

    public ProductsPage()
    {
        var u = App.User;
        using (var db = Db.Open())
        {
            _cat = Ui.Combo(Tree.CategoryOptions(db), "Tüm kategoriler", 200);
            _loc = Ui.Combo(Tree.LocationOptions(db), "Tüm lokasyonlar", 220);
            _brand = Ui.Combo(new[] { new Item(null, "Tüm markalar") }.Concat(db.Query("SELECT DISTINCT brand FROM products WHERE brand<>'' ORDER BY brand")
                .Rows.Cast<DataRow>().Select(r => new Item(0, r.Str("brand")))), 160);
            _sup = Ui.Combo(db.Query("SELECT id, name FROM suppliers WHERE active=1 ORDER BY name").Rows.Cast<DataRow>()
                .Select(r => new KeyValuePair<long, string>(r.Lng("id"), r.Str("name"))), "Tüm tedarikçiler", 180);
        }
        _q.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RefreshData(); } };
        foreach (var c in new[] { _cat, _loc, _brand, _sup }) c.SelectedIndexChanged += (_, _) => RefreshData();
        _low.CheckedChanged += (_, _) => RefreshData();
        _passive.CheckedChanged += (_, _) => RefreshData();

        var actions = new List<Control> { Ui.Btn("Detay / Aç", (_, _) => OpenSel()) };
        if (u.Can("can_edit")) actions.Add(Ui.Btn("＋ Yeni Ürün", (_, _) => { using var f = new ProductEditForm(null); if (f.ShowDialog() == DialogResult.OK) RefreshData(); }, Ui.Primary));
        if (u.Can("can_in")) actions.Add(Ui.Btn("Giriş", (_, _) => DoMove("GIRIS"), Ui.InGreen));
        if (u.Can("can_out")) actions.Add(Ui.Btn("Çıkış", (_, _) => DoMove("CIKIS"), Ui.OutRed));
        if (u.Can("can_transfer")) actions.Add(Ui.Btn("Transfer", (_, _) => DoMove("TRANSFER")));
        if (u.Can("can_count")) actions.Add(Ui.Btn("Sayım", (_, _) => DoMove("SAYIM")));
        if (u.Can("can_edit"))
        {
            actions.Add(Ui.Btn("Kategoriler", (_, _) => { using var f = new CategoriesForm(); f.ShowDialog(); RefreshData(); }));
            actions.Add(Ui.Btn("CSV İçe Aktar", (_, _) => Import()));
        }
        if (u.Can("can_report")) actions.Add(Ui.Btn("Excel'e Aktar", (_, _) => Ui.ExportCsv(_data, "stok_listesi", "id")));

        _grid.RowColors(r => r.Dbl("Miktar") <= 0 ? Ui.Danger : r.Dbl("Min.") > 0 && r.Dbl("Miktar") <= r.Dbl("Min.") ? Ui.Warn
                             : r.Lng("_active") == 0 ? Color.Gainsboro : null);
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSel(); };
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenSel(); } };

        Controls.Add(_grid);
        Controls.Add(Ui.Bar(actions.ToArray()));
        Controls.Add(Ui.Bar(_q, Ui.Btn("Ara", (_, _) => RefreshData(), Ui.Primary), _cat, _loc, _brand, _sup, _low, _passive, _count));
        Controls.Add(Ui.Title("Ürünler"));
    }

    private void OpenSel()
    {
        if (_grid.SelId() is long id) { ProductDetailForm.Open(id); RefreshData(); }
    }

    private void DoMove(string kind)
    {
        using var f = new MovementForm(kind, _grid.SelId(), null);
        f.ShowDialog();
        if (f.Saved) RefreshData();
    }

    private void Import()
    {
        using var d = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv" };
        if (d.ShowDialog() != DialogResult.OK) return;
        Ui.Run(() =>
        {
            using var db = Db.Open();
            var (added, skipped, errors) = Products.ImportCsv(db, App.User, d.FileName);
            db.Commit();
            Ui.Info($"{added} ürün eklendi, {skipped} mevcut kod atlandı." + (errors.Count > 0 ? "\n\n" + string.Join("\n", errors.Take(15)) : ""));
        });
        RefreshData();
    }

    public void RefreshData()
    {
        using var db = Db.Open();
        bool price = App.User.Can("can_see_price");
        var sql = "SELECT p.id, p.code AS Kod, p.name AS Ürün, p.brand AS Marka, p.model AS Model, c.name AS Kategori, " +
                  "(SELECT group_concat(l.code, ', ') FROM stock s JOIN locations l ON l.id=s.location_id WHERE s.product_id=p.id AND s.qty<>0) AS Lokasyon, " +
                  "p.quantity AS Miktar, p.unit AS Birim, p.min_stock AS \"Min.\", sp.name AS Tedarikçi" +
                  (price ? ", p.last_price AS \"Son Fiyat\", p.currency AS Para" : "") + ", p.active AS _active " +
                  "FROM products p LEFT JOIN categories c ON c.id=p.category_id LEFT JOIN suppliers sp ON sp.id=p.default_supplier_id WHERE 1=1";
        var args = new List<object?>();
        if (!_passive.Checked) sql += " AND p.active=1";
        var q = _q.Text.Trim();
        if (q != "")
        {
            sql += " AND (p.code LIKE ? OR p.name LIKE ? OR p.brand LIKE ? OR p.model LIKE ? OR p.description LIKE ? OR p.barcode LIKE ? OR p.tech_spec LIKE ?)";
            args.AddRange(Enumerable.Repeat<object?>($"%{q}%", 7));
        }
        if (_cat.SelId() is long cid)
        {
            var ids = Tree.SubtreeIds(db, "categories", cid);
            sql += $" AND p.category_id IN ({string.Join(",", ids)})";
        }
        if (_loc.SelId() is long lid)
        {
            var ids = Tree.SubtreeIds(db, "locations", lid);
            sql += $" AND p.id IN (SELECT product_id FROM stock WHERE qty<>0 AND location_id IN ({string.Join(",", ids)}))";
        }
        if (_brand.SelectedIndex > 0) { sql += " AND p.brand=?"; args.Add(_brand.Text); }
        if (_sup.SelId() is long sid) { sql += " AND p.default_supplier_id=?"; args.Add(sid); }
        if (_low.Checked) sql += " AND p.min_stock>0 AND p.quantity<=p.min_stock";
        _data = db.Query(sql + " ORDER BY p.code LIMIT 5000", args.ToArray());
        _grid.Bind(_data);
        _count.Text = $"{_data.Rows.Count} ürün";
    }
}
