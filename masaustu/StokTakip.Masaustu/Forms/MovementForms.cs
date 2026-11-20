using System.Data;
using StokTakip.Core;

namespace StokTakip.Masaustu.Forms;

/// <summary>Giriş / Çıkış / Transfer / Sayım penceresi. Barkod okuyucu Enter gönderdiğinde miktara geçer.</summary>
public class MovementForm : DialogBase
{
    private static readonly Dictionary<string, string> Titles = new()
    {
        ["GIRIS"] = "Stok Girişi (Mal Kabul)", ["CIKIS"] = "Stok Çıkışı", ["TRANSFER"] = "Lokasyon Transferi", ["SAYIM"] = "Sayım Düzeltme",
    };
    private readonly string _kind;
    private readonly TextBox _code = new() { Font = new Font("Segoe UI", 12f) };
    private readonly Label _info = new() { AutoSize = true, MaximumSize = new Size(470, 0), Padding = new Padding(6), BackColor = Ui.Soft };
    private readonly ComboBox _from = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _to, _sup, _cur, _project;
    private readonly TextBox _qty = new() { Font = new Font("Segoe UI", 12f) }, _doc = new(), _price = new(), _receiver = new(), _note = new();
    private readonly DateTimePicker _docDate = new() { Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
    private readonly ListBox _files = new() { Height = 60 };
    private readonly Label _locNow = new() { AutoSize = true, ForeColor = Color.DimGray };
    private DataRow? _product;
    private string _lookedUp = "\0";
    private List<(long Id, string Path, double Qty)> _stocks = new();
    public bool Saved { get; private set; }

    public MovementForm(string kind, long? pid, long? toLoc) : base(Titles[kind], 680, kind == "GIRIS" ? 720 : 600)
    {
        _kind = kind;
        using var db = Db.Open();
        var f = Ui.Fields();
        f.Add("Stok kodu / barkod *", _code);
        f.Add("", _info);
        if (kind is "CIKIS" or "TRANSFER") f.Add(kind == "CIKIS" ? "Çıkış lokasyonu *" : "Kaynak (nereden) *", _from);
        _to = Ui.Combo(Tree.LocationOptions(db), "— Seçin —");
        if (kind is "GIRIS" or "TRANSFER" or "SAYIM")
        {
            f.Add(kind switch { "GIRIS" => "Konulacağı yer (raf) *", "TRANSFER" => "Hedef (nereye) *", _ => "Sayılan lokasyon *" }, _to);
            f.Add("", _locNow);
        }
        _to.SelectId(toLoc);
        _to.SelectedIndexChanged += (_, _) => ShowLocNow();
        f.Add(kind == "SAYIM" ? "Sayılan gerçek miktar *" : "Miktar *", _qty);
        _sup = Ui.Combo(db.Query("SELECT id, name FROM suppliers WHERE active=1 ORDER BY name").Rows.Cast<DataRow>()
            .Select(r => new KeyValuePair<long, string>(r.Lng("id"), r.Str("name"))), "—");
        _cur = Ui.Combo(Perms.Currencies.Select(c => new Item(null, c)));
        _project = Ui.Combo(db.Query("SELECT DISTINCT project FROM movements WHERE project<>'' ORDER BY id DESC LIMIT 100").Rows.Cast<DataRow>()
            .Select(r => new Item(null, r.Str("project"))), 0, true);
        if (kind == "GIRIS")
        {
            f.Add("Tedarikçi", _sup);
            f.Add("Fatura / İrsaliye no", _doc);
            f.Add("Belge tarihi", _docDate);
            if (App.User.Can("can_see_price"))
            {
                f.Add("Birim fiyat (KDV hariç)", _price);
                f.Add("Para birimi", _cur);
            }
            f.Add("Fatura / irsaliye dosyası", _files);
            f.Add("", Ui.Btn("Dosya Seç (PDF / fotoğraf)…", (_, _) =>
            {
                using var d = new OpenFileDialog { Multiselect = true, Filter = "Belgeler|*.pdf;*.jpg;*.jpeg;*.png;*.webp|Tümü|*.*" };
                if (d.ShowDialog() == DialogResult.OK) _files.Items.AddRange(d.FileNames);
            }));
        }
        else if (kind == "CIKIS")
        {
            f.Add("Proje / İş emri / Makine", _project);
            f.Add("Teslim alan kişi", _receiver);
        }
        f.Add(kind == "SAYIM" ? "Açıklama (zorunlu) *" : "Açıklama", _note);
        Body.Controls.Add(f);

        var again = Ui.Btn("Kaydet ve Yeni Kalem", (_, _) => { if (OnSave()) ResetForNext(); }, null, 190);
        Buttons.Controls.Add(again);
        OkButton.BackColor = kind == "GIRIS" ? Ui.InGreen : kind == "CIKIS" ? Ui.OutRed : Ui.Primary;
        OkButton.FlatAppearance.BorderColor = OkButton.BackColor;

        using (var db2 = Db.Open())
        {
            var codes = new AutoCompleteStringCollection();
            codes.AddRange(db2.Query("SELECT code FROM products WHERE active=1").Rows.Cast<DataRow>().Select(r => r.Str("code")).ToArray());
            _code.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _code.AutoCompleteSource = AutoCompleteSource.CustomSource;
            _code.AutoCompleteCustomSource = codes;
        }
        _code.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Lookup(); _qty.Focus(); } };
        _code.Leave += (_, _) => { if (_code.Text.Trim() != _lookedUp) Lookup(); };
        _qty.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OkButton.PerformClick(); } };
        _info.Text = "Ürün kodunu yazın veya barkodu okutun";
        if (pid != null)
        {
            _code.Text = db.Scalar<string>("SELECT code FROM products WHERE id=?", pid);
            Shown += (_, _) => { Lookup(); _qty.Focus(); };
        }
    }

    private void Lookup()
    {
        _lookedUp = _code.Text.Trim();
        var keepFrom = (_from.SelectedItem as Item)?.Id;
        using var db = Db.Open();
        _product = _code.Text.Trim() == "" ? null : Stock.FindProduct(db, _code.Text);
        if (_product == null)
        {
            _info.Text = _code.Text.Trim() == "" ? "Ürün kodunu yazın veya barkodu okutun" : "⚠ Bu kodla aktif ürün bulunamadı";
            _info.BackColor = _code.Text.Trim() == "" ? Ui.Soft : Ui.Danger;
            _from.Items.Clear();
            return;
        }
        var p = _product;
        _stocks = Stock.Stocks(db, p.Lng("id"));
        var unit = p.Str("unit");
        _info.BackColor = Ui.Soft;
        _info.Text = $"{p.Str("code")} — {p.Str("name")}\n{p.Str("brand")} {p.Str("model")}\nToplam stok: {Fmt.Qty(p.Dbl("quantity"))} {unit}" +
                     (_stocks.Count > 0 ? "\n📍 " + string.Join(" | ", _stocks.Select(s => $"{s.Path}: {Fmt.Qty(s.Qty)}")) : "\n📍 Hiçbir lokasyonda yok");
        _from.Items.Clear();
        foreach (var s in _stocks.Where(s => s.Qty > 0)) _from.Items.Add(new Item(s.Id, $"{s.Path}   (mevcut {Fmt.Qty(s.Qty)} {unit})"));
        if (_from.Items.Count > 0) _from.SelectedIndex = 0;
        if (keepFrom != null) _from.SelectId(keepFrom);
        if (_to.SelId() == null && p.NLng("default_location_id") is long dl) _to.SelectId(dl);
        if (_sup.SelId() == null && p.NLng("default_supplier_id") is long ds) _sup.SelectId(ds);
        if (App.User.Can("can_see_price") && _price.Text == "" && p.Dbl("last_price") > 0)
        {
            _price.PlaceholderText = $"Son: {Fmt.Qty(p.Dbl("last_price"))} {p.Str("currency")}";
            _cur.Text = p.Str("currency");
        }
        ShowLocNow();
    }

    private void ShowLocNow()
    {
        if (_product == null || _to.SelId() is not long lid) { _locNow.Text = ""; return; }
        var q = _stocks.FirstOrDefault(s => s.Id == lid).Qty;
        _locNow.Text = $"Bu lokasyonda şu an: {Fmt.Qty(q)} {_product.Str("unit")}";
    }

    protected override bool OnSave()
    {
        if (_product == null || _code.Text.Trim() != _lookedUp) Lookup();
        if (_product == null) { Ui.Error("Ürün bulunamadı. Stok kodunu kontrol edin."); _code.Focus(); return false; }
        var qty = Fmt.ParseNum(_qty.Text);
        if (qty == null) { Ui.Error("Miktar geçerli bir sayı olmalı."); _qty.Focus(); return false; }
        if (_kind == "SAYIM" && _note.Text.Trim() == "") { Ui.Error("Sayım düzeltmesinde açıklama zorunludur."); return false; }
        var price = App.User.Can("can_see_price") ? Fmt.ParseNum(_price.Text) : null;
        if (_price.Text.Trim() != "" && price is null or < 0) { Ui.Error("Birim fiyatı kontrol edin."); return false; }
        var meta = new MoveMeta
        {
            SupplierId = _kind == "GIRIS" ? _sup.SelId() : null, UnitPrice = _kind == "GIRIS" ? price : null,
            Currency = _cur.Text == "" ? "TRY" : _cur.Text, DocumentNo = _doc.Text.Trim(),
            DocumentDate = _docDate.Checked ? _docDate.Value.ToString("yyyy-MM-dd") : "",
            Project = _project.Text.Trim(), Receiver = _receiver.Text.Trim(), Note = _note.Text.Trim(),
        };
        return Ui.Run(() =>
        {
            using var db = Db.Open();
            var (mid, after, p) = Stock.Apply(db, App.User, _product.Lng("id"), _kind, qty.Value,
                _kind is "CIKIS" or "TRANSFER" ? (_from.SelectedItem as Item)?.Id : null,
                _kind != "CIKIS" ? _to.SelId() : null, meta);
            foreach (string file in _files.Items) Attachments.Save(db, App.User, "movement", mid, file, "Fatura");
            db.Commit();
            Saved = true;
            Text = $"{Titles[_kind]}  —  ✔ {p.Str("code")} kaydedildi, toplam stok: {Fmt.Qty(after)} {p.Str("unit")}";
        });
    }

    /// <summary>Aynı belgeden ikinci kalem: tedarikçi, belge no, proje, raf korunur.</summary>
    private void ResetForNext()
    {
        _lookedUp = "\0";
        _code.Clear(); _qty.Clear(); _note.Clear(); _price.Clear(); _files.Items.Clear();
        _product = null; _from.Items.Clear(); _info.Text = "Sonraki ürünün kodunu yazın / okutun"; _info.BackColor = Ui.Soft;
        _code.Focus();
    }
}

public class MovementsPage : UserControl, IPage
{
    private readonly DataGridView _grid = Ui.Grid();
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false, Width = 130 };
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false, Width = 130 };
    private readonly ComboBox _type, _loc, _user, _sup;
    private readonly TextBox _q = new() { Width = 220, PlaceholderText = "Kod, ürün, belge, proje, kişi" };
    private DataTable _data = new();

    public MovementsPage()
    {
        using var db = Db.Open();
        _type = Ui.Combo(new[] { new Item(null, "Tüm tipler") }.Concat(Perms.MoveLabel.Select((kv, i) => new Item(i, kv.Value))), 130);
        _loc = Ui.Combo(Tree.LocationOptions(db, false), "Tüm lokasyonlar", 220);
        _user = Ui.Combo(db.Query("SELECT id, full_name FROM users ORDER BY full_name").Rows.Cast<DataRow>()
            .Select(r => new KeyValuePair<long, string>(r.Lng("id"), r.Str("full_name"))), "Tüm kullanıcılar", 170);
        _sup = Ui.Combo(db.Query("SELECT id, name FROM suppliers ORDER BY name").Rows.Cast<DataRow>()
            .Select(r => new KeyValuePair<long, string>(r.Lng("id"), r.Str("name"))), "Tüm tedarikçiler", 170);
        _q.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RefreshData(); } };
        _grid.RowColors(r => r.Str("Tip") switch { "Giriş" => Color.FromArgb(236, 248, 240), "Çıkış" => Color.FromArgb(253, 240, 238), _ => null });
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && _grid.SelId() is long id) { using var f = new MovementDetailForm(id); f.ShowDialog(); } };
        Controls.Add(_grid);
        Controls.Add(Ui.Bar(Ui.Lbl("Başlangıç"), _from, Ui.Lbl("Bitiş"), _to, _type, _loc, _user, _sup, _q,
            Ui.Btn("Filtrele", (_, _) => RefreshData(), Ui.Primary),
            Ui.Btn("Excel'e Aktar", (_, _) => Ui.ExportCsv(_data, "hareketler", "id", "product_id"))));
        Controls.Add(Ui.Title("Stok Hareketleri"));
    }

    /// <summary>Ham hareket satırlarını ekranda/Excel'de gösterilecek Türkçe kolonlara çevirir.</summary>
    public static DataTable Shape(DbSession db, DataTable raw)
    {
        var paths = Tree.Paths(db, "locations");
        bool price = App.User.Can("can_see_price");
        var t = new DataTable();
        foreach (var c in new[] { "id", "product_id", "Tarih", "Tip", "Kod", "Ürün", "Miktar", "Birim", "Nereden", "Nereye", "Tedarikçi",
                                  "Belge No", "Proje / Makine", "Teslim Alan", "Talep", "Açıklama", "Kullanıcı" })
            t.Columns.Add(c, typeof(object));
        if (price) { t.Columns.Add("Birim Fiyat", typeof(object)); t.Columns.Add("Para", typeof(object)); }
        foreach (DataRow r in raw.Rows)
        {
            var type = r.Str("type");
            var qty = r.Dbl("qty") * (type == "CIKIS" ? -1 : 1);
            var row = new List<object?>
            {
                r.Lng("id"), r.Lng("product_id"), r.Str("created_at")[..Math.Min(16, r.Str("created_at").Length)], Perms.MoveLabel[type],
                r.Str("code"), r.Str("name"), qty, r.Str("unit"),
                r.NLng("from_location_id") is long f ? paths.GetValueOrDefault(f, "") : "",
                r.NLng("to_location_id") is long to ? paths.GetValueOrDefault(to, "") : "",
                r.Str("supplier_name"), r.Str("document_no"), r.Str("project"), r.Str("receiver"),
                r.NLng("request_id") is long rid ? Perms.ReqNo(rid, r.Str("created_at")) : "", r.Str("note"), r.Str("full_name"),
            };
            if (price) { row.Add(r.NDbl("unit_price") is double up ? up : DBNull.Value); row.Add(r.Str("currency")); }
            t.Rows.Add(row.Select(x => x ?? DBNull.Value).ToArray());
        }
        return t;
    }

    public void RefreshData()
    {
        using var db = Db.Open();
        var sql = Stock.MoveSelect + " WHERE 1=1";
        var args = new List<object?>();
        if (_from.Checked) { sql += " AND m.created_at >= ?"; args.Add(_from.Value.ToString("yyyy-MM-dd")); }
        if (_to.Checked) { sql += " AND m.created_at < date(?, '+1 day')"; args.Add(_to.Value.ToString("yyyy-MM-dd")); }
        if (_type.SelectedIndex > 0) { sql += " AND m.type=?"; args.Add(Perms.MoveLabel.Keys.ElementAt(_type.SelectedIndex - 1)); }
        if (_user.SelId() is long uid) { sql += " AND m.user_id=?"; args.Add(uid); }
        if (_sup.SelId() is long sid) { sql += " AND m.supplier_id=?"; args.Add(sid); }
        if (_loc.SelId() is long lid)
        {
            var ids = string.Join(",", Tree.SubtreeIds(db, "locations", lid));
            sql += $" AND (m.from_location_id IN ({ids}) OR m.to_location_id IN ({ids}))";
        }
        if (_q.Text.Trim() != "")
        {
            sql += " AND (p.code LIKE ? OR p.name LIKE ? OR m.document_no LIKE ? OR m.project LIKE ? OR m.note LIKE ? OR m.receiver LIKE ?)";
            args.AddRange(Enumerable.Repeat<object?>($"%{_q.Text.Trim()}%", 6));
        }
        _data = Shape(db, db.Query(sql + " ORDER BY m.id DESC LIMIT 5000", args.ToArray()));
        _grid.Bind(_data);
    }
}

public class MovementDetailForm : DialogBase
{
    public MovementDetailForm(long mid) : base("Hareket Detayı", 720, 640, "Tamam")
    {
        CancelBtn.Visible = false;
        using var db = Db.Open();
        var m = db.Row(Stock.MoveSelect + " WHERE m.id=?", mid) ?? throw new StockException("Hareket bulunamadı.");
        var paths = Tree.Paths(db, "locations");
        var f = Ui.Fields();
        void Line(string k, string v) { if (v != "") f.Add(k, new Label { Text = v, AutoSize = true, MaximumSize = new Size(460, 0), Font = Ui.Bold }); }
        Line("Tip", Perms.MoveLabel[m.Str("type")] + $"   (#{mid})");
        Line("Tarih", m.Str("created_at"));
        Line("Ürün", $"{m.Str("code")} — {m.Str("name")}");
        Line("Miktar", $"{Fmt.Qty(m.Dbl("qty"))} {m.Str("unit")}   (toplam {Fmt.Qty(m.Dbl("qty_before"))} → {Fmt.Qty(m.Dbl("qty_after"))})");
        Line("Nereden", m.NLng("from_location_id") is long fl ? paths.GetValueOrDefault(fl, "") : "");
        Line("Nereye", m.NLng("to_location_id") is long tl ? paths.GetValueOrDefault(tl, "") : "");
        Line("Tedarikçi", m.Str("supplier_name"));
        Line("Belge", $"{m.Str("document_no")} {m.Str("document_date")}".Trim());
        if (App.User.Can("can_see_price") && m.NDbl("unit_price") is double up)
            Line("Birim fiyat", $"{Fmt.Money(up, m.Str("currency"))}   (tutar {Fmt.Money(up * m.Dbl("qty"), m.Str("currency"))})");
        Line("Proje / Makine", m.Str("project"));
        Line("Teslim alan", m.Str("receiver"));
        Line("Talep", m.NLng("request_id") is long rid ? Perms.ReqNo(rid, m.Str("created_at")) : "");
        Line("Açıklama", m.Str("note"));
        Line("İşlemi yapan", m.Str("full_name"));
        if (App.User.CanAny("can_see_price", "can_purchase", "can_in"))
        {
            var files = new AttachmentsPanel("movement", mid, App.User.CanAny("can_in", "can_purchase")) { Dock = DockStyle.Fill };
            files.Reload();
            Body.Controls.Add(files);
            Body.Controls.Add(new Label { Text = "📎 Fatura / İrsaliye Dosyaları", Font = Ui.H2, Dock = DockStyle.Top, Height = 30, ForeColor = Ui.Primary });
        }
        Body.Controls.Add(f);
    }
}
