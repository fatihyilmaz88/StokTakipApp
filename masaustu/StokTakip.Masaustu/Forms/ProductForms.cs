using System.Data;
using StokTakip.Core;

namespace StokTakip.Masaustu.Forms;

public class ProductEditForm : DialogBase
{
    private readonly ProductData _d;
    private readonly TextBox _code, _barcode, _name, _model, _desc, _spec, _min, _max, _price, _initial;
    private readonly ComboBox _brand, _unit, _cat, _loc, _sup, _cur;
    private readonly CheckBox _active = new() { Text = "Aktif (kaldırılırsa listede gizlenir, geçmişi silinmez)", AutoSize = true };
    public long SavedId { get; private set; }

    /// <param name="copyFrom">"Kopyala": benzer ürünün bilgileriyle yeni kayıt</param>
    public ProductEditForm(long? id, long? copyFrom = null) : base(id == null ? "Yeni Ürün" : "Ürün Düzenle", 760, 760)
    {
        using var db = Db.Open();
        var src = id ?? copyFrom;
        _d = src != null ? ProductData.From(db.Row("SELECT * FROM products WHERE id=?", src)!) : new ProductData();
        if (id == null) { _d.Id = null; _d.Code = ""; _d.Barcode = ""; }

        var f = Ui.Fields();
        _code = f.Add("Stok kodu *", Ui.Text(_d.Code));
        _barcode = f.Add("Barkod (üretici, ops.)", Ui.Text(_d.Barcode));
        _name = f.Add("Ürün adı *", Ui.Text(_d.Name));
        _brand = f.Add("Marka / Üretici", Ui.Combo(db.Query("SELECT DISTINCT brand FROM products WHERE brand<>'' ORDER BY 1").Rows.Cast<DataRow>()
            .Select(r => new Item(null, r.Str("brand"))), 0, true));
        _brand.Text = _d.Brand;
        _model = f.Add("Model / Parça no (MPN)", Ui.Text(_d.Model));
        _cat = f.Add("Kategori", Ui.Combo(Tree.CategoryOptions(db), "—"));
        _cat.SelectId(_d.CategoryId);
        _unit = f.Add("Birim", Ui.Combo(Perms.Units.Select(x => new Item(null, x)), 0, true));
        _unit.Text = _d.Unit;
        _desc = f.Add("Açıklama", Ui.Text(_d.Description, true));
        _spec = f.Add("Teknik özellikler", Ui.Text(_d.TechSpec, true));
        _spec.PlaceholderText = "ör. 24VDC, 32A, IP65, 250 bar, DN16…";
        _loc = f.Add("Varsayılan lokasyon (rafı)", Ui.Combo(Tree.LocationOptions(db), "—"));
        _loc.SelectId(_d.DefaultLocationId);
        _sup = f.Add("Ana tedarikçi", Ui.Combo(db.Query("SELECT id, name FROM suppliers WHERE active=1 ORDER BY name").Rows.Cast<DataRow>()
            .Select(r => new KeyValuePair<long, string>(r.Lng("id"), r.Str("name"))), "—"));
        _sup.SelectId(_d.DefaultSupplierId);
        _min = f.Add("Minimum stok (kritik)", Ui.Text(Fmt.Qty(_d.MinStock)));
        _max = f.Add("Maksimum stok (sipariş hedefi)", Ui.Text(Fmt.Qty(_d.MaxStock)));
        _price = Ui.Text(Fmt.Qty(_d.LastPrice ?? 0));
        _cur = Ui.Combo(Perms.Currencies.Select(c => new Item(null, c)));
        _cur.Text = _d.Currency;
        if (App.User.Can("can_see_price"))
        {
            f.Add("Son alış fiyatı", _price);
            f.Add("Para birimi", _cur);
        }
        _initial = Ui.Text("0");
        if (id == null) f.Add("Başlangıç miktarı", _initial);
        else { _active.Checked = _d.Active; f.Add("", _active); }
        Body.Controls.Add(f);
        if (id != null) Body.Controls.Add(new Label { Text = "Miktar buradan değişmez: izlenebilirlik için Giriş / Çıkış / Transfer / Sayım kullanın.", Dock = DockStyle.Bottom, Height = 24, ForeColor = Color.DimGray });
        else Body.Controls.Add(new Label { Text = "Fotoğraf, datasheet ve teknik resimleri kaydettikten sonra ürün kartındaki 'Dosyalar' sekmesinden ekleyin.", Dock = DockStyle.Bottom, Height = 24, ForeColor = Color.DimGray });
    }

    protected override bool OnSave()
    {
        double? min = Fmt.ParseNum(_min.Text, 0), max = Fmt.ParseNum(_max.Text, 0), price = Fmt.ParseNum(_price.Text, 0), init = Fmt.ParseNum(_initial.Text, 0);
        if (min == null || max == null || price == null || init == null) { Ui.Error("Sayı alanlarını kontrol edin."); return false; }
        _d.Code = _code.Text; _d.Barcode = _barcode.Text; _d.Name = _name.Text; _d.Brand = _brand.Text; _d.Model = _model.Text;
        _d.CategoryId = _cat.SelId(); _d.Unit = _unit.Text; _d.Description = _desc.Text; _d.TechSpec = _spec.Text;
        _d.DefaultLocationId = _loc.SelId(); _d.DefaultSupplierId = _sup.SelId(); _d.MinStock = min.Value; _d.MaxStock = max.Value;
        _d.LastPrice = App.User.Can("can_see_price") ? price : null; _d.Currency = _cur.Text;
        if (_d.Id != null) _d.Active = _active.Checked;
        return Ui.Run(() =>
        {
            using var db = Db.Open();
            SavedId = Products.Save(db, App.User, _d, init.Value);
            db.Commit();
        });
    }
}

public class ProductDetailForm : Form
{
    private readonly long _pid;
    private readonly Label _code = new() { Font = Ui.H1, AutoSize = true, ForeColor = Ui.Primary };
    private readonly Label _name = new() { AutoSize = true, Font = Ui.H2 };
    private readonly Label _bm = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Label _qty = new() { Font = Ui.Big, AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    private readonly PictureBox _img = new() { Width = 120, Height = 120, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Visible = false };
    private readonly TextBox _info = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Font = new Font("Consolas", 10f) };
    private readonly DataGridView _stocks = Ui.Grid(), _moves = Ui.Grid(), _buys = Ui.Grid(), _usage = Ui.Grid(), _reqs = Ui.Grid();
    private readonly AttachmentsPanel _files;
    private readonly FlowLayoutPanel _actions = new() { AutoSize = true, Dock = DockStyle.Top };

    public static void Open(long pid)
    {
        using var f = new ProductDetailForm(pid);
        f.ShowDialog();
    }

    public ProductDetailForm(long pid)
    {
        _pid = pid;
        Text = "Ürün Kartı"; Width = 1100; Height = 780; Font = Ui.Base; StartPosition = FormStartPosition.CenterParent; BackColor = Color.White;
        _files = new AttachmentsPanel("product", pid, App.User.CanAny("can_edit", "can_in", "can_purchase")) { Dock = DockStyle.Fill };
        _files.Changed += LoadData;

        var head = new TableLayoutPanel { Dock = DockStyle.Top, Height = 140, ColumnCount = 3, Padding = new Padding(12) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var names = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        names.Controls.AddRange(new Control[] { _code, _name, _bm });
        head.Controls.Add(_img, 0, 0);
        head.Controls.Add(names, 1, 0);
        head.Controls.Add(_qty, 2, 0);
        _img.Click += (_, _) => { if (_img.Tag is string p) Ui.OpenFile(p); };

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
        Tab(tabs, "📍 Lokasyonlar", _stocks);
        Tab(tabs, "Bilgiler", _info);
        Tab(tabs, "📎 Dosyalar", _files);
        Tab(tabs, "Hareketler", _moves);
        Tab(tabs, "🔧 Nerede Kullanıldı", _usage);
        Tab(tabs, "Açık Talepler", _reqs);
        if (App.User.Can("can_see_price")) Tab(tabs, "💰 Satın Alma", _buys);
        _moves.CellDoubleClick += (_, _) => { if (_moves.SelId() is long mid) { using var f = new MovementDetailForm(mid); f.ShowDialog(); } };
        _reqs.CellDoubleClick += (_, _) => { if (_reqs.SelId() is long rid) { using var f = new RequestDetailForm(rid); f.ShowDialog(); LoadData(); } };

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 12) };
        body.Controls.Add(tabs);
        body.Controls.Add(_actions);
        Controls.Add(body);
        Controls.Add(head);
        Load += (_, _) => LoadData();
    }

    private static void Tab(TabControl t, string title, Control c)
    {
        var p = new TabPage(title) { Padding = new Padding(8), BackColor = Color.White };
        c.Dock = DockStyle.Fill;
        p.Controls.Add(c);
        t.TabPages.Add(p);
    }

    private void Act(string text, Action a, Color? c = null) => _actions.Controls.Add(Ui.Btn(text, (_, _) => { a(); LoadData(); }, c));

    private void LoadData()
    {
        using var db = Db.Open();
        var u = App.User;
        var p = db.Row("SELECT p.*, s.name AS supplier_name, us.full_name AS creator FROM products p LEFT JOIN suppliers s ON s.id=p.default_supplier_id " +
                       "LEFT JOIN users us ON us.id=p.created_by WHERE p.id=?", _pid);
        if (p == null) { Close(); return; }
        var unit = p.Str("unit");
        _code.Text = p.Str("code") + (p.Bool("active") ? "" : "   (PASİF)");
        _name.Text = p.Str("name");
        _bm.Text = $"{p.Str("brand")} {p.Str("model")}".Trim();
        _qty.Text = $"{Fmt.Qty(p.Dbl("quantity"))} {unit}";
        _qty.BackColor = p.Dbl("quantity") <= 0 ? Ui.Danger : p.Dbl("min_stock") > 0 && p.Dbl("quantity") <= p.Dbl("min_stock") ? Ui.Warn : Ui.Soft;

        _img.Visible = false;
        if (p.NLng("image_id") is long imgId && db.Row("SELECT stored_name FROM attachments WHERE id=?", imgId) is DataRow im)
        {
            var path = Attachments.FullPath(im.Str("stored_name"));
            if (File.Exists(path))
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    _img.Image?.Dispose();
                    _img.Image = Image.FromStream(fs);
                    _img.Tag = path; _img.Visible = true;
                }
                catch { /* resim okunamazsa gösterme */ }
            }
        }

        _actions.Controls.Clear();
        if (p.Bool("active"))
        {
            if (u.Can("can_in")) Act("＋ Giriş", () => DoMove("GIRIS"), Ui.InGreen);
            if (u.Can("can_out")) Act("－ Çıkış", () => DoMove("CIKIS"), Ui.OutRed);
            if (u.Can("can_transfer")) Act("⇄ Transfer", () => DoMove("TRANSFER"));
            if (u.Can("can_count")) Act("Sayım", () => DoMove("SAYIM"));
        }
        if (u.Can("can_edit"))
        {
            Act("Düzenle", () => { using var f = new ProductEditForm(_pid); f.ShowDialog(this); });
            Act("Kopyala (benzer ürün)", () =>
            {
                using var f = new ProductEditForm(null, _pid);
                if (f.ShowDialog(this) == DialogResult.OK) { Hide(); Open(f.SavedId); Close(); }
            });
        }

        var paths = Tree.Paths(db, "locations");
        var cat = p.NLng("category_id") is long cid ? Tree.Paths(db, "categories").GetValueOrDefault(cid, "-") : "-";
        var lines = new List<string>
        {
            $"Kategori        : {cat}",
            $"Barkod          : {(p.Str("barcode") == "" ? "-" : p.Str("barcode"))}",
            $"Varsayılan yer  : {(p.NLng("default_location_id") is long dl ? paths.GetValueOrDefault(dl, "-") : "-")}",
            $"Ana tedarikçi   : {(p.Str("supplier_name") == "" ? "-" : p.Str("supplier_name"))}",
            $"Min. / Maks.    : {Fmt.Qty(p.Dbl("min_stock"))} / {Fmt.Qty(p.Dbl("max_stock"))} {unit}",
        };
        if (u.Can("can_see_price"))
        {
            lines.Add($"Son alış fiyatı : {Fmt.Money(p.Dbl("last_price"), p.Str("currency"))}");
            lines.Add($"Stok değeri     : {Fmt.Money(p.Dbl("last_price") * p.Dbl("quantity"), p.Str("currency"))}");
        }
        lines.Add($"Oluşturan       : {p.Str("creator")}  {p.Str("created_at")[..Math.Min(10, p.Str("created_at").Length)]}");
        if (p.Str("description") != "") { lines.Add(""); lines.Add("AÇIKLAMA"); lines.Add(p.Str("description")); }
        if (p.Str("tech_spec") != "") { lines.Add(""); lines.Add("TEKNİK ÖZELLİKLER"); lines.Add(p.Str("tech_spec")); }
        _info.Text = string.Join(Environment.NewLine, lines.Select(l => l.Replace("\n", Environment.NewLine)));

        var st = new DataTable();
        st.Columns.Add("Lokasyon"); st.Columns.Add("Miktar", typeof(object)); st.Columns.Add("Birim");
        foreach (var s in Stock.Stocks(db, _pid)) st.Rows.Add(s.Path, s.Qty, unit);
        if (st.Rows.Count == 0) st.Rows.Add("Stokta yok", 0.0, unit);
        _stocks.Bind(st);
        _moves.Bind(MovementsPage.Shape(db, db.Query(Stock.MoveSelect + " WHERE m.product_id=? ORDER BY m.id DESC LIMIT 300", _pid)));
        _usage.Bind(db.Query("SELECT COALESCE(NULLIF(project,''),'(belirtilmemiş)') AS \"Proje / Makine\", SUM(qty) AS Miktar FROM movements " +
                             "WHERE product_id=? AND type='CIKIS' AND created_at>=date('now','-365 day') GROUP BY project ORDER BY 2 DESC", _pid));
        _reqs.Bind(db.Query("SELECT r.id, 'MT-'||substr(r.created_at,1,4)||'-'||printf('%05d',r.id) AS No, u.full_name AS \"Talep Eden\", r.project AS Proje, " +
                            "ri.qty-ri.issued_qty AS Kalan FROM request_items ri JOIN requests r ON r.id=ri.request_id JOIN users u ON u.id=r.requester_id " +
                            "WHERE ri.product_id=? AND r.status IN ('BEKLIYOR','ONAYLANDI','KISMI') AND ri.qty>ri.issued_qty", _pid));
        if (u.Can("can_see_price"))
            _buys.Bind(db.Query("SELECT substr(m.created_at,1,10) AS Tarih, s.name AS Tedarikçi, m.document_no AS Belge, m.qty AS Miktar, " +
                                "m.unit_price AS \"Birim Fiyat\", m.currency AS Para FROM movements m LEFT JOIN suppliers s ON s.id=m.supplier_id " +
                                "WHERE m.product_id=? AND m.type='GIRIS' AND m.unit_price IS NOT NULL ORDER BY m.id DESC LIMIT 50", _pid));
        _files.Reload();
    }

    private void DoMove(string kind)
    {
        using var f = new MovementForm(kind, _pid, null);
        f.ShowDialog(this);
    }
}

/// <summary>Bir kayda bağlı dosyalar: ekle, aç, klasörde göster, sil.</summary>
public class AttachmentsPanel : UserControl
{
    private readonly string _entity;
    private readonly long _id;
    private readonly DataGridView _grid = Ui.Grid();
    private readonly ComboBox _kind = Ui.Combo(Perms.AttachKinds.Select(k => new Item(null, k)), 150);
    public event Action? Changed;

    public AttachmentsPanel(string entity, long id, bool allowUpload)
    {
        _entity = entity; _id = id;
        var bar = new List<Control> { Ui.Btn("Aç", (_, _) => OpenSel()), Ui.Btn("Klasörde Göster", (_, _) => ShowInFolder()) };
        if (allowUpload)
        {
            bar.Insert(0, _kind);
            bar.Insert(1, Ui.Btn("＋ Dosya Ekle", (_, _) => AddFiles(), Ui.Primary));
            bar.Add(Ui.Btn("Sil", (_, _) => DeleteSel(), Ui.OutRed));
        }
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSel(); };
        // Dosyaları pencereye sürükleyip bırakarak da eklenebilir
        if (allowUpload)
        {
            _grid.AllowDrop = true;
            _grid.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            _grid.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) Save(files); };
        }
        Controls.Add(_grid);
        Controls.Add(Ui.Bar(bar.ToArray()));
        Controls.Add(new Label { Text = "PDF, resim, Excel, Word, DWG/DXF, STEP, EPLAN, ZIP… (en fazla 40 MB). Dosyaları listeye sürükleyip bırakabilirsiniz.", Dock = DockStyle.Bottom, ForeColor = Color.DimGray, Height = 22 });
    }

    public void Reload()
    {
        using var db = Db.Open();
        var t = Attachments.List(db, _entity, _id);
        foreach (var r in t.Rows.Cast<DataRow>().ToList())
            if (!Attachments.CanView(App.User, r.Str("entity"), r.Str("Tür"))) t.Rows.Remove(r);
        _grid.Bind(t);
    }

    private void AddFiles()
    {
        using var d = new OpenFileDialog { Multiselect = true, Title = "Eklenecek dosyaları seçin" };
        if (d.ShowDialog() == DialogResult.OK) Save(d.FileNames);
    }

    private void Save(IEnumerable<string> files)
    {
        Ui.Run(() =>
        {
            using var db = Db.Open();
            foreach (var f in files) Attachments.Save(db, App.User, _entity, _id, f, _kind.Text);
        });
        Reload();
        Changed?.Invoke();
    }

    private void OpenSel()
    {
        if (_grid.SelRow() is DataRow r) Ui.OpenFile(Attachments.FullPath(r.Str("stored_name")));
    }

    private void ShowInFolder()
    {
        if (_grid.SelRow() is DataRow r)
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Attachments.FullPath(r.Str("stored_name"))}\"");
    }

    private void DeleteSel()
    {
        if (_grid.SelRow() is not DataRow r || !Ui.Confirm($"'{r.Str("Dosya")}' silinsin mi?")) return;
        Ui.Run(() => { using var db = Db.Open(); Attachments.Delete(db, App.User, r.Lng("id")); });
        Reload();
        Changed?.Invoke();
    }
}

public class CategoriesForm : DialogBase
{
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, Font = Ui.Base, HideSelection = false };

    public CategoriesForm() : base("Kategoriler", 560, 600, "Tamam")
    {
        CancelBtn.Visible = false;
        Body.Controls.Add(_tree);
        Body.Controls.Add(Ui.Bar(
            Ui.Btn("＋ Ana Kategori", (_, _) => AddCat(null), Ui.Primary),
            Ui.Btn("＋ Alt Kategori", (_, _) => { if (_tree.SelectedNode?.Tag is long id) AddCat(id); else Ui.Error("Önce bir kategori seçin."); }),
            Ui.Btn("Yeniden Adlandır", (_, _) => Rename()),
            Ui.Btn("Sil", (_, _) => Delete(), Ui.OutRed)));
        Body.Controls.Add(new Label { Text = "Örnek: Hidrolik › Valf, Elektrik › Kontaktör, Pnömatik, Rulman, Sarf Malzeme…", Dock = DockStyle.Top, Height = 24, ForeColor = Color.DimGray });
        LoadTree();
    }

    private void LoadTree()
    {
        using var db = Db.Open();
        var rows = db.Query("SELECT c.id, c.parent_id, c.name, (SELECT COUNT(*) FROM products WHERE category_id=c.id AND active=1) n FROM categories c ORDER BY c.name").Rows.Cast<DataRow>().ToList();
        _tree.Nodes.Clear();
        void Fill(TreeNodeCollection nodes, long? parent)
        {
            foreach (var r in rows.Where(r => r.NLng("parent_id") == parent))
            {
                var n = nodes.Add($"{r.Str("name")}   ({r.Lng("n")} ürün)");
                n.Tag = r.Lng("id");
                Fill(n.Nodes, r.Lng("id"));
            }
        }
        Fill(_tree.Nodes, null);
        _tree.ExpandAll();
    }

    private void AddCat(long? parent)
    {
        var name = Ui.Prompt("Kategori", "Kategori adı:");
        if (string.IsNullOrWhiteSpace(name)) return;
        Ui.Run(() =>
        {
            using var db = Db.Open();
            if (db.Scalar<long>("SELECT COUNT(*) FROM categories WHERE name=? AND parent_id IS ?", name.Trim(), parent) > 0)
                throw new StockException("Bu isimde kategori zaten var.");
            db.Exec("INSERT INTO categories (name, parent_id) VALUES (?,?)", name.Trim(), parent);
            Audit.Log(db, App.User, "Kategori eklendi", name);
        });
        LoadTree();
    }

    private void Rename()
    {
        if (_tree.SelectedNode?.Tag is not long id) return;
        var name = Ui.Prompt("Kategori", "Yeni ad:", _tree.SelectedNode.Text.Split("   (")[0]);
        if (string.IsNullOrWhiteSpace(name)) return;
        Ui.Run(() => { using var db = Db.Open(); db.Exec("UPDATE categories SET name=? WHERE id=?", name.Trim(), id); });
        LoadTree();
    }

    private void Delete()
    {
        if (_tree.SelectedNode?.Tag is not long id || !Ui.Confirm("Kategori silinsin mi?")) return;
        Ui.Run(() =>
        {
            using var db = Db.Open();
            if (db.Scalar<long>("SELECT COUNT(*) FROM products WHERE category_id=?", id) > 0 || db.Scalar<long>("SELECT COUNT(*) FROM categories WHERE parent_id=?", id) > 0)
                throw new StockException("İçinde ürün veya alt kategori olan kategori silinemez.");
            db.Exec("DELETE FROM categories WHERE id=?", id);
        });
        LoadTree();
    }
}
