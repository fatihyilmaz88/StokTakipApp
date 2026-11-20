using System.Data;
using StokTakip.Core;

namespace StokTakip.Masaustu.Forms;

/// <summary>Depo › Bölge › Raf › Göz ağacı (solda) ve seçili lokasyonun içeriği (sağda).</summary>
public class LocationsPage : UserControl, IPage
{
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, Font = Ui.Base, HideSelection = false, BorderStyle = BorderStyle.None };
    private readonly DataGridView _grid = Ui.Grid();
    private readonly Label _title = new() { Font = Ui.H2, Dock = DockStyle.Top, Height = 32, ForeColor = Ui.Primary };
    private readonly Label _desc = new() { Dock = DockStyle.Top, Height = 24, ForeColor = Color.DimGray };

    public LocationsPage()
    {
        var u = App.User;
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 360, BackColor = Color.White };
        split.Panel1.Controls.Add(_tree);
        split.Panel1.Padding = new Padding(6);
        var actions = new List<Control>();
        if (u.Can("can_in")) actions.Add(Ui.Btn("＋ Buraya Giriş", (_, _) => { if (Sel() is long id) { using var f = new MovementForm("GIRIS", null, id); f.ShowDialog(); LoadContents(); } }, Ui.InGreen));
        if (u.Can("can_edit"))
        {
            actions.Add(Ui.Btn("＋ Depo (en üst)", (_, _) => Edit(null, null), Ui.Primary));
            actions.Add(Ui.Btn("＋ Alt Lokasyon", (_, _) => { if (Sel() is long id) Edit(null, id); }));
            actions.Add(Ui.Btn("Düzenle", (_, _) => { if (Sel() is long id) Edit(id, null); }));
            actions.Add(Ui.Btn("Seri Raf / Göz Oluştur", (_, _) => Bulk()));
        }
        actions.Add(Ui.Btn("Excel'e Aktar", (_, _) => { if (_grid.DataSource is DataTable t) Ui.ExportCsv(t, "lokasyon_icerigi", "product_id"); }));
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && _grid.SelId("product_id") is long pid) { ProductDetailForm.Open(pid); LoadContents(); } };
        split.Panel2.Padding = new Padding(10, 0, 0, 0);
        split.Panel2.Controls.Add(_grid);
        split.Panel2.Controls.Add(Ui.Bar(actions.ToArray()));
        split.Panel2.Controls.Add(_desc);
        split.Panel2.Controls.Add(_title);
        _tree.AfterSelect += (_, _) => LoadContents();
        Controls.Add(split);
        Controls.Add(new Label { Dock = DockStyle.Top, Height = 24, ForeColor = Color.DimGray, Text = "Örnek yapı: Ana Ambar › A Bölgesi › Raf A-03 › Göz A-03-02. Bir lokasyon seçildiğinde alt lokasyonlarıyla birlikte içeriği listelenir." });
        Controls.Add(Ui.Title("Ambar Lokasyonları"));
    }

    private long? Sel()
    {
        if (_tree.SelectedNode?.Tag is long id) return id;
        Ui.Error("Önce soldan bir lokasyon seçin.");
        return null;
    }

    public void RefreshData()
    {
        var keep = _tree.SelectedNode?.Tag as long?;
        using var db = Db.Open();
        var rows = db.Query("SELECT l.*, (SELECT COUNT(*) FROM stock s WHERE s.location_id=l.id AND s.qty<>0) n FROM locations l ORDER BY l.code").Rows.Cast<DataRow>().ToList();
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        TreeNode? toSelect = null;
        void Fill(TreeNodeCollection nodes, long? parent)
        {
            foreach (var r in rows.Where(r => r.NLng("parent_id") == parent))
            {
                var n = nodes.Add($"{r.Str("code")}  –  {r.Str("name")}" + (r.Lng("n") > 0 ? $"   ({r.Lng("n")})" : ""));
                n.Tag = r.Lng("id");
                if (!r.Bool("active")) n.ForeColor = Color.Gray;
                if (r.Lng("id") == keep) toSelect = n;
                Fill(n.Nodes, r.Lng("id"));
            }
        }
        Fill(_tree.Nodes, null);
        _tree.ExpandAll();
        _tree.EndUpdate();
        _tree.SelectedNode = toSelect ?? (_tree.Nodes.Count > 0 ? _tree.Nodes[0] : null);
        LoadContents();
    }

    private void LoadContents()
    {
        if (_tree.SelectedNode?.Tag is not long id) return;
        using var db = Db.Open();
        var l = db.Row("SELECT * FROM locations WHERE id=?", id)!;
        _title.Text = $"{l.Str("type")}: {l.Str("code")}  ·  {Tree.Paths(db, "locations")[id]}" + (l.Bool("active") ? "" : "  (PASİF)");
        _desc.Text = l.Str("description");
        var ids = string.Join(",", Tree.SubtreeIds(db, "locations", id));
        _grid.Bind(db.Query("SELECT s.product_id, lo.code AS Lokasyon, p.code AS Kod, p.name AS Ürün, p.brand AS Marka, p.model AS Model, " +
                            $"s.qty AS Miktar, p.unit AS Birim FROM stock s JOIN products p ON p.id=s.product_id JOIN locations lo ON lo.id=s.location_id " +
                            $"WHERE s.location_id IN ({ids}) AND s.qty<>0 ORDER BY lo.code, p.code"));
    }

    private void Edit(long? id, long? parent)
    {
        using var f = new LocationEditForm(id, parent);
        if (f.ShowDialog() == DialogResult.OK) RefreshData();
    }

    private void Bulk()
    {
        if (Sel() is not long parent) return;
        var input = Ui.Prompt("Seri Raf / Göz", "Önek, başlangıç, bitiş, tip   (örnek: G,1,10,Göz → KOD-G01…KOD-G10)", "R,1,10,Raf");
        if (input == null) return;
        var parts = input.Split(',').Select(x => x.Trim()).ToArray();
        if (parts.Length < 3 || !int.TryParse(parts[1], out var a) || !int.TryParse(parts[2], out var b) || b < a || b - a > 200)
        { Ui.Error("Biçim: önek,başlangıç,bitiş,tip (en fazla 200 adet)."); return; }
        var type = parts.Length > 3 && parts[3] != "" ? parts[3] : "Göz";
        Ui.Run(() =>
        {
            using var db = Db.Open();
            var pcode = db.Scalar<string>("SELECT code FROM locations WHERE id=?", parent);
            int width = Math.Max(2, b.ToString().Length), n = 0;
            db.Begin();
            for (int i = a; i <= b; i++)
            {
                var suffix = parts[0].ToUpperInvariant() + i.ToString().PadLeft(width, '0');
                if (db.Scalar<long>("SELECT COUNT(*) FROM locations WHERE code=?", $"{pcode}-{suffix}") > 0) continue;
                db.Exec("INSERT INTO locations (code, name, type, parent_id) VALUES (?,?,?,?)", $"{pcode}-{suffix}", $"{type} {suffix}", type, parent);
                n++;
            }
            Audit.Log(db, App.User, "Toplu lokasyon", $"{pcode} altına {n} adet");
            db.Commit();
            Ui.Info($"{n} lokasyon oluşturuldu.");
        });
        RefreshData();
    }
}

public class LocationEditForm : DialogBase
{
    private readonly long? _id;
    private readonly TextBox _code, _name, _desc;
    private readonly ComboBox _parent, _type;
    private readonly CheckBox _active = new() { Text = "Aktif", AutoSize = true, Checked = true };

    public LocationEditForm(long? id, long? parent) : base(id == null ? "Yeni Lokasyon" : "Lokasyon Düzenle", 620, 380)
    {
        _id = id;
        using var db = Db.Open();
        var l = id != null ? db.Row("SELECT * FROM locations WHERE id=?", id) : null;
        var exclude = id != null ? Tree.SubtreeIds(db, "locations", id.Value).ToHashSet() : new HashSet<long>();
        var f = Ui.Fields();
        _parent = f.Add("Üst lokasyon", Ui.Combo(Tree.LocationOptions(db, false).Where(o => !exclude.Contains(o.Key)), "— En üst (Depo/Bina) —"));
        _parent.SelectId(l?.NLng("parent_id") ?? parent);
        _code = f.Add("Kod * (etikette yazar)", Ui.Text(l?.Str("code") ?? ""));
        _type = f.Add("Tip", Ui.Combo(Perms.LocTypes.Select(t => new Item(null, t))));
        _type.Text = l?.Str("type") ?? (parent == null ? "Depo" : "Raf");
        _name = f.Add("Ad *", Ui.Text(l?.Str("name") ?? ""));
        _desc = f.Add("Açıklama", Ui.Text(l?.Str("description") ?? ""));
        if (l != null) { _active.Checked = l.Bool("active"); f.Add("", _active); }
        Body.Controls.Add(f);
    }

    protected override bool OnSave() => Ui.Run(() =>
    {
        var code = _code.Text.Trim().ToUpperInvariant();
        if (code == "" || _name.Text.Trim() == "") throw new StockException("Kod ve ad zorunlu.");
        using var db = Db.Open();
        if (db.Scalar<long>("SELECT COUNT(*) FROM locations WHERE code=? AND id<>?", code, _id ?? -1) > 0) throw new StockException("Bu lokasyon kodu zaten var.");
        if (_id != null && !_active.Checked && db.Scalar<long>("SELECT COUNT(*) FROM stock WHERE location_id=? AND qty<>0", _id) > 0)
            throw new StockException("İçinde stok olan lokasyon pasif yapılamaz. Önce transfer edin.");
        if (_id != null)
            db.Exec("UPDATE locations SET code=?, name=?, type=?, description=?, parent_id=?, active=? WHERE id=?",
                code, _name.Text.Trim(), _type.Text, _desc.Text.Trim(), _parent.SelId(), _active.Checked ? 1 : 0, _id);
        else
            db.Exec("INSERT INTO locations (code, name, type, description, parent_id) VALUES (?,?,?,?,?)",
                code, _name.Text.Trim(), _type.Text, _desc.Text.Trim(), _parent.SelId());
        Audit.Log(db, App.User, "Lokasyon kaydedildi", $"{code} - {_name.Text.Trim()}");
    });
}

public class SuppliersPage : UserControl, IPage
{
    private readonly DataGridView _grid = Ui.Grid();
    private readonly TextBox _q = new() { Width = 220, PlaceholderText = "Firma, yetkili, telefon…" };

    public SuppliersPage()
    {
        var bar = new List<Control> { _q, Ui.Btn("Ara", (_, _) => RefreshData(), Ui.Primary), Ui.Btn("Aç", (_, _) => OpenSel()) };
        if (App.User.Can("can_purchase")) bar.Insert(0, Ui.Btn("＋ Yeni Tedarikçi", (_, _) => { using var f = new SupplierForm(null); if (f.ShowDialog() == DialogResult.OK) RefreshData(); }, Ui.Primary));
        _q.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RefreshData(); } };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSel(); };
        _grid.RowColors(r => r.Lng("_active") == 0 ? Color.Gainsboro : null);
        Controls.Add(_grid);
        Controls.Add(Ui.Bar(bar.ToArray()));
        Controls.Add(Ui.Title("Tedarikçiler"));
    }

    private void OpenSel()
    {
        if (_grid.SelId() is not long id) return;
        using var f = new SupplierForm(id);
        f.ShowDialog();
        RefreshData();
    }

    public void RefreshData()
    {
        using var db = Db.Open();
        var q = $"%{_q.Text.Trim()}%";
        _grid.Bind(db.Query("SELECT s.id, s.name AS Firma, s.contact AS Yetkili, s.phone AS Telefon, s.email AS \"E-posta\", " +
                            "(SELECT COUNT(*) FROM products WHERE default_supplier_id=s.id) AS Ürün, " +
                            "substr((SELECT MAX(created_at) FROM movements WHERE supplier_id=s.id),1,10) AS \"Son Alım\", s.active AS _active " +
                            "FROM suppliers s WHERE s.name LIKE ? OR s.contact LIKE ? OR s.phone LIKE ? ORDER BY s.active DESC, s.name", q, q, q));
    }
}

/// <summary>Tedarikçi bilgileri (düzenlenebilir) + verdiği ürünler + alımlar + dosyalar.</summary>
public class SupplierForm : DialogBase
{
    private readonly long? _id;
    private readonly Dictionary<string, TextBox> _f = new();
    private readonly CheckBox _active = new() { Text = "Aktif", AutoSize = true, Checked = true };
    private static readonly (string Key, string Label, bool Multi)[] Fields =
    {
        ("name", "Firma adı *", false), ("contact", "Yetkili kişi", false), ("phone", "Telefon", false), ("email", "E-posta", false),
        ("website", "Web sitesi", false), ("tax_office", "Vergi dairesi", false), ("tax_no", "Vergi no", false),
        ("address", "Adres", true), ("note", "Not (vade, temsil ettiği markalar…)", true),
    };

    public SupplierForm(long? id) : base(id == null ? "Yeni Tedarikçi" : "Tedarikçi", 900, 720)
    {
        _id = id;
        bool edit = App.User.Can("can_purchase");
        OkButton.Visible = edit;
        using var db = Db.Open();
        var s = id != null ? db.Row("SELECT * FROM suppliers WHERE id=?", id) : null;
        var form = Ui.Fields();
        foreach (var (k, l, m) in Fields)
        {
            var tb = Ui.Text(s?.Str(k) ?? "", m);
            tb.ReadOnly = !edit;
            _f[k] = form.Add(l, tb);
        }
        if (s != null) { _active.Checked = s.Bool("active"); _active.Enabled = edit; form.Add("", _active); }
        if (id == null) { Body.Controls.Add(form); return; }

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var info = new TabPage("Bilgiler") { AutoScroll = true, Padding = new Padding(8) };
        info.Controls.Add(form);
        tabs.TabPages.Add(info);
        var prods = Ui.Grid();
        prods.Bind(db.Query("SELECT id, code AS Kod, name AS Ürün, quantity AS Miktar, unit AS Birim" +
                            (App.User.Can("can_see_price") ? ", last_price AS \"Son Fiyat\", currency AS Para" : "") +
                            " FROM products WHERE default_supplier_id=? ORDER BY code", id));
        prods.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && prods.SelId() is long pid) ProductDetailForm.Open(pid); };
        AddTab(tabs, "Verdiği Ürünler", prods);
        var buys = Ui.Grid();
        buys.Bind(MovementsPage.Shape(db, db.Query(Stock.MoveSelect + " WHERE m.supplier_id=? ORDER BY m.id DESC LIMIT 300", id)));
        buys.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && buys.SelId() is long mid) { using var f = new MovementDetailForm(mid); f.ShowDialog(); } };
        AddTab(tabs, "Alımlar", buys);
        var files = new AttachmentsPanel("supplier", id.Value, edit);
        files.Reload();
        AddTab(tabs, "📎 Teklif / Sözleşme / Belgeler", files);
        Body.Controls.Add(tabs);
    }

    private static void AddTab(TabControl t, string title, Control c)
    {
        var p = new TabPage(title) { Padding = new Padding(6) };
        c.Dock = DockStyle.Fill;
        p.Controls.Add(c);
        t.TabPages.Add(p);
    }

    protected override bool OnSave() => Ui.Run(() =>
    {
        var v = Fields.Select(f => (object?)_f[f.Key].Text.Trim()).ToList();
        if ((string)v[0]! == "") throw new StockException("Firma adı zorunlu.");
        using var db = Db.Open();
        if (db.Scalar<long>("SELECT COUNT(*) FROM suppliers WHERE name=? AND id<>?", v[0], _id ?? -1) > 0) throw new StockException("Bu isimde tedarikçi zaten var.");
        var cols = Fields.Select(f => f.Key).ToList();
        if (_id != null)
            db.Exec($"UPDATE suppliers SET {string.Join(", ", cols.Select(c => c + "=?"))}, active=? WHERE id=?", v.Append(_active.Checked ? 1 : 0).Append(_id).ToArray());
        else
            db.Exec($"INSERT INTO suppliers ({string.Join(",", cols)}, created_at) VALUES ({string.Join(",", cols.Select(_ => "?"))},?)", v.Append(Db.Now()).ToArray());
        Audit.Log(db, App.User, "Tedarikçi kaydedildi", (string)v[0]!);
    });
}
