using System.Data;
using StokTakip.Core;

namespace StokTakip.Masaustu.Forms;

public class RequestsPage : UserControl, IPage
{
    private readonly DataGridView _grid = Ui.Grid();
    private readonly ComboBox _status;
    private readonly CheckBox _mine = new() { Text = "Sadece benimkiler", AutoSize = true, Margin = new Padding(8, 8, 8, 0) };
    private readonly bool _seeAll = App.User.CanAny("can_approve", "can_out", "can_report");

    public RequestsPage()
    {
        _status = Ui.Combo(new[] { new Item(null, "Açık talepler") }
            .Concat(Perms.ReqStatus.Select((kv, i) => new Item(i, kv.Value)))
            .Append(new Item(-1, "Tümü")), 200);
        _status.SelectedIndexChanged += (_, _) => RefreshData();
        _mine.CheckedChanged += (_, _) => RefreshData();
        _mine.Visible = _seeAll;
        _grid.RowColors(r => r.Str("Öncelik") == "Acil" && r.Str("_st") is "BEKLIYOR" or "ONAYLANDI" or "KISMI" ? Ui.Warn : null);
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSel(); };
        var bar = new List<Control> { _status, _mine, Ui.Btn("Aç", (_, _) => OpenSel()) };
        if (App.User.Can("can_request"))
            bar.Insert(0, Ui.Btn("📝 Yeni Talep", (_, _) => { using var f = new RequestNewForm(); if (f.ShowDialog() == DialogResult.OK) RefreshData(); }, Ui.Primary));
        Controls.Add(_grid);
        Controls.Add(Ui.Bar(bar.ToArray()));
        Controls.Add(new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Color.DimGray,
            Text = "Akış: Talep (üretim / tasarım / bakım) → Onay (şef / müdür) → Ambar teslimi (raftan düşer)" });
        Controls.Add(Ui.Title("Malzeme Talepleri"));
    }

    private void OpenSel()
    {
        if (_grid.SelId() is not long id) return;
        using var f = new RequestDetailForm(id);
        f.ShowDialog();
        RefreshData();
    }

    public void RefreshData()
    {
        using var db = Db.Open();
        var sql = "SELECT r.id, 'MT-'||substr(r.created_at,1,4)||'-'||printf('%05d',r.id) AS No, substr(r.created_at,1,16) AS Tarih, " +
                  "u.full_name AS \"Talep Eden\", r.department AS Departman, r.project AS \"Proje / Makine\", r.priority AS Öncelik, " +
                  "r.needed_date AS \"İhtiyaç\", (SELECT COUNT(*) FROM request_items WHERE request_id=r.id) AS Kalem, " +
                  "CASE r.status " + string.Join(" ", Perms.ReqStatus.Select(kv => $"WHEN '{kv.Key}' THEN '{kv.Value}'")) + " END AS Durum, " +
                  "a.full_name AS Onaylayan, r.status AS _st FROM requests r JOIN users u ON u.id=r.requester_id " +
                  "LEFT JOIN users a ON a.id=r.approver_id WHERE 1=1";
        var args = new List<object?>();
        if (!_seeAll || _mine.Checked) { sql += " AND r.requester_id=?"; args.Add(App.User.Id); }
        var sel = _status.SelectedItem as Item;
        if (sel?.Id == null) sql += " AND r.status IN ('BEKLIYOR','ONAYLANDI','KISMI')";
        else if (sel.Id >= 0) { sql += " AND r.status=?"; args.Add(Perms.ReqStatus.Keys.ElementAt((int)sel.Id.Value)); }
        sql += " ORDER BY CASE r.status WHEN 'BEKLIYOR' THEN 0 WHEN 'ONAYLANDI' THEN 1 WHEN 'KISMI' THEN 2 ELSE 3 END, r.id DESC LIMIT 1000";
        _grid.Bind(db.Query(sql, args.ToArray()));
    }
}

public class RequestNewForm : DialogBase
{
    private readonly ComboBox _project, _priority;
    private readonly DateTimePicker _needed = new() { Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false };
    private readonly TextBox _note = Ui.Text("", true);
    private readonly DataGridView _items = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = true, RowHeadersVisible = false, BackgroundColor = Color.White, Font = Ui.Base,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EditMode = DataGridViewEditMode.EditOnEnter,
    };
    private readonly ListBox _files = new() { Height = 50, Dock = DockStyle.Bottom };
    private readonly Dictionary<string, long> _codes = new(StringComparer.OrdinalIgnoreCase);

    public RequestNewForm() : base("Yeni Malzeme Talebi", 860, 680, "Talebi Gönder")
    {
        using var db = Db.Open();
        foreach (DataRow r in db.Query("SELECT id, code FROM products WHERE active=1").Rows) _codes[r.Str("code")] = r.Lng("id");
        var f = Ui.Fields();
        _project = f.Add("Proje / İş emri / Makine *", Ui.Combo(db.Query("SELECT DISTINCT project FROM requests ORDER BY id DESC LIMIT 100")
            .Rows.Cast<DataRow>().Select(r => new Item(null, r.Str("project"))), 0, true));
        f.Add("İhtiyaç tarihi", _needed);
        _priority = f.Add("Öncelik", Ui.Combo(new[] { "Normal", "Acil", "Düşük" }.Select(x => new Item(null, x))));
        f.Add("Açıklama", _note);

        _items.Columns.Add(new DataGridViewTextBoxColumn { Name = "code", HeaderText = "Stok Kodu", FillWeight = 25 });
        _items.Columns.Add(new DataGridViewTextBoxColumn { Name = "qty", HeaderText = "Miktar", FillWeight = 12 });
        _items.Columns.Add(new DataGridViewTextBoxColumn { Name = "note", HeaderText = "Not", FillWeight = 25 });
        _items.Columns.Add(new DataGridViewTextBoxColumn { Name = "info", HeaderText = "Ürün / Stok", ReadOnly = true, FillWeight = 38, DefaultCellStyle = { ForeColor = Color.DimGray } });
        _items.EditingControlShowing += (_, e) =>
        {
            if (e.Control is TextBox tb && _items.CurrentCell?.ColumnIndex == 0)
            {
                var ac = new AutoCompleteStringCollection();
                ac.AddRange(_codes.Keys.ToArray());
                tb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                tb.AutoCompleteSource = AutoCompleteSource.CustomSource;
                tb.AutoCompleteCustomSource = ac;
            }
            else if (e.Control is TextBox t2) t2.AutoCompleteMode = AutoCompleteMode.None;
        };
        _items.CellEndEdit += (_, e) => { if (e.ColumnIndex == 0) ShowInfo(_items.Rows[e.RowIndex]); };

        var itemsBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
        itemsBox.Controls.Add(_items);
        itemsBox.Controls.Add(new Label { Text = "Kalemler (her satıra bir ürün; kodu yazmaya başlayınca öneri gelir)", Dock = DockStyle.Top, Height = 24, Font = Ui.Bold });
        itemsBox.Controls.Add(_files);
        itemsBox.Controls.Add(Ui.Bar(Ui.Btn("Ek dosya (teknik resim, liste)…", (_, _) =>
        {
            using var d = new OpenFileDialog { Multiselect = true };
            if (d.ShowDialog() == DialogResult.OK) _files.Items.AddRange(d.FileNames);
        })).Also(b => b.Dock = DockStyle.Bottom));
        Body.Controls.Add(itemsBox);
        Body.Controls.Add(f);
    }

    private void ShowInfo(DataGridViewRow row)
    {
        var code = Convert.ToString(row.Cells["code"].Value)?.Trim() ?? "";
        if (code == "") { row.Cells["info"].Value = ""; return; }
        using var db = Db.Open();
        var p = Stock.FindProduct(db, code);
        row.Cells["info"].Value = p == null ? "⚠ ürün bulunamadı" : $"{p.Str("name")} {p.Str("brand")} {p.Str("model")} — stok {Fmt.Qty(p.Dbl("quantity"))} {p.Str("unit")}";
    }

    protected override bool OnSave()
    {
        _items.EndEdit();
        var items = new List<(long, double, string)>();
        using var db = Db.Open();
        foreach (DataGridViewRow row in _items.Rows)
        {
            if (row.IsNewRow) continue;
            var code = Convert.ToString(row.Cells["code"].Value)?.Trim() ?? "";
            if (code == "") continue;
            var p = Stock.FindProduct(db, code);
            var q = Fmt.ParseNum(Convert.ToString(row.Cells["qty"].Value));
            if (p == null) { Ui.Error($"'{code}' kodlu ürün bulunamadı."); return false; }
            if (q is null or <= 0) { Ui.Error($"{code}: miktar geçersiz."); return false; }
            items.Add((p.Lng("id"), q.Value, Convert.ToString(row.Cells["note"].Value)?.Trim() ?? ""));
        }
        return Ui.Run(() =>
        {
            var rid = Requests.Create(db, App.User, _project.Text, _needed.Checked ? _needed.Value.ToString("yyyy-MM-dd") : "",
                _priority.Text, _note.Text, items);
            foreach (string file in _files.Items) Attachments.Save(db, App.User, "request", rid, file, "Teknik Resim");
            db.Commit();
            Ui.Info($"{Perms.ReqNo(rid, Db.Now())} numaralı talep oluşturuldu ve onaya gönderildi.");
        });
    }
}

public class RequestDetailForm : DialogBase
{
    private readonly long _rid;
    private readonly Label _head = new() { Font = Ui.H1, ForeColor = Ui.Primary, AutoSize = true };
    private readonly Label _flow = new() { AutoSize = true, Font = Ui.Bold, Padding = new Padding(8), BackColor = Ui.Soft, Margin = new Padding(0, 6, 0, 6) };
    private readonly Label _info = new() { AutoSize = true, MaximumSize = new Size(920, 0) };
    private readonly DataGridView _items = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false, BackgroundColor = Color.White, Font = Ui.Base,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EditMode = DataGridViewEditMode.EditOnEnter,
    };
    private readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly TextBox _receiver = new() { Width = 220 };
    private readonly DataGridView _moves = Ui.Grid();
    private readonly AttachmentsPanel _files;
    private bool _issuing;

    public RequestDetailForm(long rid) : base("Malzeme Talebi", 1000, 760, "Tamam")
    {
        _rid = rid;
        CancelBtn.Visible = false;
        _items.DataError += (_, e) => e.ThrowException = false;
        _files = new AttachmentsPanel("request", rid, true) { Dock = DockStyle.Fill };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        top.Controls.AddRange(new Control[] { _head, _flow, _info });
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var t1 = new TabPage("Kalemler") { Padding = new Padding(6) };
        t1.Controls.Add(_items);
        var t2 = new TabPage("📎 Ekler") { Padding = new Padding(6) };
        t2.Controls.Add(_files);
        var t3 = new TabPage("Teslimat Hareketleri") { Padding = new Padding(6) };
        t3.Controls.Add(_moves);
        tabs.TabPages.AddRange(new[] { t1, t2, t3 });
        Body.Controls.Add(tabs);
        Body.Controls.Add(_actions);
        Body.Controls.Add(top);
        Load += (_, _) => LoadData();
    }

    private void LoadData()
    {
        using var db = Db.Open();
        var r = Requests.Get(db, _rid);
        if (!Requests.CanView(App.User, r)) { Ui.Error("Bu talebi görme yetkiniz yok."); Close(); return; }
        var status = r.Str("status");
        var no = Perms.ReqNo(_rid, r.Str("created_at"));
        _head.Text = $"{no}   {r.Str("project")}" + (r.Str("priority") == "Acil" ? "   [ACİL]" : "");
        string Step(bool done, bool now, string t) => (done ? "✔ " : now ? "► " : "○ ") + t;
        var approved = status is "ONAYLANDI" or "KISMI" or "TAMAMLANDI";
        _flow.Text = $"{Step(true, false, "Talep: " + r.Str("full_name"))}      " +
                     (status is "REDDEDILDI" or "IPTAL" ? "✕ " + Perms.ReqStatus[status] + (r.Str("approver") != "" ? ": " + r.Str("approver") : "")
                         : Step(approved, status == "BEKLIYOR", "Onay" + (r.Str("approver") != "" ? ": " + r.Str("approver") : ""))) + "      " +
                     Step(status == "TAMAMLANDI", status is "ONAYLANDI" or "KISMI", "Ambar teslim" + (status == "KISMI" ? " (kısmi)" : "")) +
                     $"        Durum: {Perms.ReqStatus[status]}";
        _info.Text = $"Talep eden: {r.Str("full_name")} {(r.Str("department") != "" ? "(" + r.Str("department") + ")" : "")}   ·   " +
                     $"Tarih: {r.Str("created_at")[..16]}   ·   İhtiyaç: {(r.Str("needed_date") == "" ? "-" : r.Str("needed_date"))}" +
                     (r.Str("note") != "" ? $"\nAçıklama: {r.Str("note")}" : "") +
                     (r.Str("decision_note") != "" ? $"\nOnaylayan notu: {r.Str("decision_note")}" : "");

        _issuing = status is "ONAYLANDI" or "KISMI" && App.User.Can("can_out");
        _items.Columns.Clear();
        _items.Rows.Clear();
        _items.Columns.Add(new DataGridViewTextBoxColumn { Name = "id", Visible = false });
        foreach (var (n, h, w) in new[] { ("code", "Kod", 14), ("name", "Ürün", 30), ("qty", "İstenen", 9), ("issued", "Verilen", 9), ("stock", "Stok", 9) })
            _items.Columns.Add(new DataGridViewTextBoxColumn { Name = n, HeaderText = h, ReadOnly = true, FillWeight = w });
        if (_issuing)
        {
            _items.Columns.Add(new DataGridViewComboBoxColumn { Name = "loc", HeaderText = "Hangi raftan", FillWeight = 30, DisplayMember = "Text", ValueMember = "Id", FlatStyle = FlatStyle.Flat });
            _items.Columns.Add(new DataGridViewTextBoxColumn { Name = "give", HeaderText = "Teslim miktarı", FillWeight = 12, DefaultCellStyle = { BackColor = Color.LightYellow } });
        }
        foreach (DataRow it in Requests.Items(db, _rid).Rows)
        {
            var rem = it.Dbl("qty") - it.Dbl("issued_qty");
            var idx = _items.Rows.Add(it.Lng("id"), it.Str("code"), $"{it.Str("name")}  {it.Str("brand")} {it.Str("model")}  {it.Str("note")}",
                $"{Fmt.Qty(it.Dbl("qty"))} {it.Str("unit")}", Fmt.Qty(it.Dbl("issued_qty")), Fmt.Qty(it.Dbl("quantity")));
            var row = _items.Rows[idx];
            if (it.Dbl("quantity") < rem && rem > 0) row.DefaultCellStyle.BackColor = Ui.Danger;
            if (!_issuing) continue;
            var cell = (DataGridViewComboBoxCell)row.Cells["loc"];
            var stocks = Stock.Stocks(db, it.Lng("product_id"), true).OrderByDescending(s => s.Qty).ToList();
            if (rem <= 1e-9 || stocks.Count == 0)
            {
                cell.DataSource = new List<Item> { new(0, rem <= 1e-9 ? "✔ tamamlandı" : "STOK YOK") };
                cell.Value = 0L;
                row.Cells["give"].ReadOnly = true;
                row.Cells["give"].Value = "";
                continue;
            }
            cell.DataSource = stocks.Select(s => new Item(s.Id, $"{s.Path} ({Fmt.Qty(s.Qty)})")).ToList();
            cell.Value = stocks[0].Id;
            row.Cells["give"].Value = Fmt.Qty(Math.Min(rem, stocks[0].Qty));
        }

        _actions.Controls.Clear();
        if (status == "BEKLIYOR" && App.User.Can("can_approve"))
        {
            _actions.Controls.Add(Ui.Btn("✔ Onayla", (_, _) => Decide(true), Ui.InGreen));
            _actions.Controls.Add(Ui.Btn("✕ Reddet", (_, _) => Decide(false), Ui.OutRed));
        }
        if (_issuing)
        {
            _receiver.Text = r.Str("full_name");
            _actions.Controls.Add(Ui.Lbl("Teslim alan:"));
            _actions.Controls.Add(_receiver);
            _actions.Controls.Add(Ui.Btn("Seçilen Miktarları Teslim Et (stoktan düş)", (_, _) => Issue(), Ui.OutRed));
            if (status == "KISMI")
                _actions.Controls.Add(Ui.Btn("Kalanı Verme, Talebi Kapat", (_, _) =>
                {
                    if (Ui.Confirm("Kalan miktarlar verilmeden talep kapatılsın mı?") && Ui.Run(() => { using var d = Db.Open(); Requests.Close(d, App.User, _rid); })) LoadData();
                }));
        }
        if (status is "BEKLIYOR" or "ONAYLANDI" && (r.Lng("requester_id") == App.User.Id || App.User.IsAdmin))
            _actions.Controls.Add(Ui.Btn("Talebi İptal Et", (_, _) =>
            {
                if (Ui.Confirm("Talep iptal edilsin mi?") && Ui.Run(() => { using var d = Db.Open(); Requests.Cancel(d, App.User, _rid); })) LoadData();
            }));
        _moves.Bind(MovementsPage.Shape(db, db.Query(Stock.MoveSelect + " WHERE m.request_id=? ORDER BY m.id", _rid)));
        _files.Reload();
    }

    private void Decide(bool approve)
    {
        var note = Ui.Prompt(approve ? "Onay" : "Red", approve ? "Not (isteğe bağlı):" : "Red gerekçesi (zorunlu):");
        if (note == null) return;
        if (Ui.Run(() => { using var db = Db.Open(); Requests.Decide(db, App.User, _rid, approve, note); })) LoadData();
    }

    private void Issue()
    {
        _items.EndEdit();
        var list = new List<(long, double, long?)>();
        foreach (DataGridViewRow row in _items.Rows)
        {
            var txt = Convert.ToString(row.Cells["give"].Value);
            if (string.IsNullOrWhiteSpace(txt)) continue;
            var q = Fmt.ParseNum(txt);
            if (q == null) { Ui.Error($"{row.Cells["code"].Value}: miktar geçersiz."); return; }
            var loc = row.Cells["loc"].Value is long l && l > 0 ? l : (long?)null;
            list.Add((Convert.ToInt64(row.Cells["id"].Value), q.Value, loc));
        }
        string st = "";
        if (Ui.Run(() =>
            {
                using var db = Db.Open();
                st = Requests.Issue(db, App.User, _rid, list, _receiver.Text);
                db.Commit();
            }))
            Ui.Info("Teslim kaydedildi. Durum: " + Perms.ReqStatus[st]);
        LoadData();
    }
}

internal static class ControlExt
{
    public static T Also<T>(this T c, Action<T> a) { a(c); return c; }
}
