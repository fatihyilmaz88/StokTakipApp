using System.Data;
using StokTakip.Core;

namespace StokTakip.Masaustu.Forms;

public class ReportsPage : UserControl, IPage
{
    private readonly ListBox _list = new() { Dock = DockStyle.Left, Width = 250, Font = Ui.Base, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly DataGridView _grid = Ui.Grid();
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short, Width = 130, Value = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1) };
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short, Width = 130 };
    private readonly ComboBox _group = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly NumericUpDown _days = new() { Minimum = 0, Maximum = 3650, Value = 180, Width = 80 };
    private readonly Label _note = new() { Dock = DockStyle.Top, Height = 24, ForeColor = Color.DimGray };
    private readonly FlowLayoutPanel _params;
    private DataTable _data = new();
    private readonly List<(string Name, string Perm, Func<DbSession, DataTable> Run, string Params, string Note)> _reports = new();

    public ReportsPage()
    {
        var u = App.User;
        if (u.Can("can_report"))
        {
            _reports.Add(("Tüketim Raporu", "", db => Reports.Consumption(db, u, D(_from), D(_to), GroupKey()), "date,group",
                "Seçilen tarihler arasında hangi proje/makineye, kime ne kadar malzeme çıktı."));
            _reports.Add(("Sipariş Önerisi", "", db => Reports.Reorder(db, u), "",
                "Önerilen = (Maks. stok veya 2×Min.) − Mevcut + Açık talepler"));
            _reports.Add(("Hareketsiz Stok", "", db => Reports.Dead(db, u, (int)_days.Value), "days",
                "Belirtilen gün sayısı boyunca giriş/çıkış olmayan, rafta para bağlayan malzemeler."));
        }
        if (u.Can("can_see_price"))
            _reports.Add(("Stok Değeri", "", db => Reports.Value(db, _group.SelectedIndex == 1 ? "lokasyon" : "kategori"), "valuegroup",
                "Son alış fiyatıyla envanter değeri (para birimine göre ayrı toplam)."));
        if (u.IsAdmin)
            _reports.Add(("Denetim Kaydı", "", Reports.AuditLog, "", "Kim, ne zaman, neyi değiştirdi (son 2000 kayıt)."));
        foreach (var r in _reports) _list.Items.Add(r.Name);
        _list.SelectedIndexChanged += (_, _) => { SetParams(); RefreshData(); };
        _group.SelectedIndexChanged += (_, _) => RefreshData();

        _params = Ui.Bar(Ui.Lbl("Başlangıç"), _from, Ui.Lbl("Bitiş"), _to, Ui.Lbl("Gruplama"), _group, Ui.Lbl("Gün"), _days,
            Ui.Btn("Göster", (_, _) => RefreshData(), Ui.Primary), Ui.Btn("Excel'e Aktar", (_, _) => Ui.ExportCsv(_data, "rapor")));
        _grid.RowColors(r => r.Table.Columns.Count > 0 && Convert.ToString(r[0]) == "TOPLAM" ? Ui.Soft : null);
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 0, 0) };
        right.Controls.Add(_grid);
        right.Controls.Add(_note);
        right.Controls.Add(_params);
        Controls.Add(right);
        Controls.Add(_list);
        Controls.Add(Ui.Title("Raporlar"));
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private static string D(DateTimePicker p) => p.Value.ToString("yyyy-MM-dd");

    private string GroupKey() => _group.SelectedIndex switch { 1 => "urun", 2 => "kullanici", 3 => "departman", 4 => "teslim", _ => "proje" };

    private void SetParams()
    {
        if (_list.SelectedIndex < 0) return;
        var p = _reports[_list.SelectedIndex].Params;
        _group.Items.Clear();
        if (p.Contains("group")) _group.Items.AddRange(new object[] { "Proje / Makine bazında", "Ürün bazında", "Çıkışı yapan bazında", "Departman bazında", "Teslim alan bazında" });
        if (p.Contains("valuegroup")) _group.Items.AddRange(new object[] { "Kategori bazında", "Lokasyon bazında" });
        if (_group.Items.Count > 0) _group.SelectedIndex = 0;
        foreach (Control c in _params.Controls)
        {
            if (c == _from || c == _to || (c is Label l && l.Text is "Başlangıç" or "Bitiş")) c.Visible = p.Contains("date");
            if (c == _group || (c is Label g && g.Text == "Gruplama")) c.Visible = _group.Items.Count > 0;
            if (c == _days || (c is Label d && d.Text == "Gün")) c.Visible = p.Contains("days");
        }
        _note.Text = _reports[_list.SelectedIndex].Note;
    }

    public void RefreshData()
    {
        if (_list.SelectedIndex < 0) return;
        using var db = Db.Open();
        _data = _reports[_list.SelectedIndex].Run(db);
        _grid.Bind(_data);
    }
}

public class UsersPage : UserControl, IPage
{
    private readonly DataGridView _grid = Ui.Grid();

    public UsersPage()
    {
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Edit(_grid.SelId()); };
        _grid.RowColors(r => r.Lng("_active") == 0 ? Color.Gainsboro : null);
        Controls.Add(_grid);
        Controls.Add(Ui.Bar(Ui.Btn("＋ Yeni Kullanıcı", (_, _) => Edit(null), Ui.Primary), Ui.Btn("Düzenle", (_, _) => { if (_grid.SelId() is long id) Edit(id); })));
        Controls.Add(Ui.Title("Kullanıcılar"));
    }

    private void Edit(long? id)
    {
        using var f = new UserEditForm(id);
        if (f.ShowDialog() == DialogResult.OK) RefreshData();
    }

    public void RefreshData()
    {
        using var db = Db.Open();
        var t = db.Query("SELECT * FROM users ORDER BY active DESC, full_name");
        var v = new DataTable();
        foreach (var c in new[] { "id", "Ad Soyad", "Kullanıcı", "Rol", "Departman", "Yetkiler", "Son Giriş", "Durum", "_active" }) v.Columns.Add(c, typeof(object));
        foreach (DataRow r in t.Rows)
            v.Rows.Add(r.Lng("id"), r.Str("full_name"), r.Str("username"), r.Str("role"), r.Str("department"),
                r.Bool("is_admin") ? "YÖNETİCİ" : string.Join(", ", Perms.All.Where(p => r.Bool(p.Key)).Select(p => p.Label.Split(" (")[0].Split(" /")[0])) is var s && s != "" ? s : "Sadece görüntüleme",
                r.Str("last_login") is var ll && ll.Length >= 16 ? ll[..16] : "-", r.Bool("active") ? "Aktif" : "Pasif", r.Lng("active"));
        _grid.Bind(v);
    }
}

public class UserEditForm : DialogBase
{
    private readonly long? _id;
    private readonly TextBox _user, _name, _phone, _pw;
    private readonly ComboBox _dept, _role;
    private readonly Dictionary<string, CheckBox> _perms = new();
    private readonly CheckBox _active = new() { Text = "Hesap aktif", AutoSize = true, Checked = true };

    public UserEditForm(long? id) : base(id == null ? "Yeni Kullanıcı" : "Kullanıcı Düzenle", 640, 720)
    {
        _id = id;
        using var db = Db.Open();
        var u = id != null ? db.Row("SELECT * FROM users WHERE id=?", id) : null;
        var f = Ui.Fields();
        _user = f.Add("Kullanıcı adı *", Ui.Text(u?.Str("username") ?? ""));
        _name = f.Add("Ad soyad *", Ui.Text(u?.Str("full_name") ?? ""));
        _dept = f.Add("Departman", Ui.Combo(new[] { "Üretim", "Tasarım", "Bakım", "Ambar", "Satın Alma", "Kalite", "Muhasebe", "Yönetim" }
            .Union(db.Query("SELECT DISTINCT department FROM users WHERE department<>''").Rows.Cast<DataRow>().Select(r => r.Str("department")))
            .Select(x => new Item(null, x)), 0, true));
        _dept.Text = u?.Str("department") ?? "";
        _phone = f.Add("Telefon", Ui.Text(u?.Str("phone") ?? ""));
        _pw = f.Add(id == null ? "Geçici şifre *" : "Yeni şifre (boşsa değişmez)", Ui.Text());
        _pw.UseSystemPasswordChar = true;
        _role = f.Add("Rol şablonu", Ui.Combo(new[] { new Item(null, "— Özel —") }.Concat(Perms.Roles.Keys.Select(k => new Item(null, k)))));
        if (u != null && Perms.Roles.ContainsKey(u.Str("role"))) _role.Text = u.Str("role");
        _role.SelectedIndexChanged += (_, _) =>
        {
            if (!Perms.Roles.TryGetValue(_role.Text, out var keys)) return;
            foreach (var (k, cb) in _perms) cb.Checked = keys.Contains(k);
        };
        var box = new GroupBox { Text = "Yetkiler (herkes stokları ve ürün kartlarını görüntüleyebilir)", Dock = DockStyle.Top, Height = 34 + 28 * Perms.All.Length, Padding = new Padding(10) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
        foreach (var (k, label) in Perms.All)
        {
            var cb = new CheckBox { Text = label, AutoSize = true, Checked = u?.Bool(k) ?? false };
            _perms[k] = cb;
            flow.Controls.Add(cb);
        }
        box.Controls.Add(flow);
        _active.Checked = u?.Bool("active") ?? true;
        Body.Controls.Add(_active.Also(c => c.Dock = DockStyle.Top));
        Body.Controls.Add(box);
        Body.Controls.Add(new Label { Text = "Kullanıcı ilk girişinde kendi şifresini belirlemek zorundadır.", Dock = DockStyle.Top, Height = 24, ForeColor = Color.DimGray });
        Body.Controls.Add(f);
    }

    protected override bool OnSave() => Ui.Run(() =>
    {
        var username = _user.Text.Trim();
        var name = _name.Text.Trim();
        if (username == "" || name == "") throw new StockException("Kullanıcı adı ve ad soyad zorunlu.");
        if ((_id == null || _pw.Text != "") && _pw.Text.Length < 6) throw new StockException("Şifre en az 6 karakter olmalı.");
        if (_id == App.User.Id && (!_perms["is_admin"].Checked || !_active.Checked)) throw new StockException("Kendi yönetici yetkinizi veya hesabınızı kapatamazsınız.");
        var role = _role.SelectedIndex > 0 ? _role.Text : "";
        var perms = Perms.Keys.Select(k => (object?)(_perms[k].Checked ? 1 : 0)).ToArray();
        using var db = Db.Open();
        if (db.Scalar<long>("SELECT COUNT(*) FROM users WHERE username=? AND id<>?", username, _id ?? -1) > 0) throw new StockException("Bu kullanıcı adı zaten var.");
        db.Begin();
        if (_id != null)
        {
            db.Exec("UPDATE users SET username=?, full_name=?, department=?, role=?, phone=?, active=?, " +
                    string.Join(", ", Perms.Keys.Select(k => k + "=?")) + " WHERE id=?",
                new object?[] { username, name, _dept.Text.Trim(), role, _phone.Text.Trim(), _active.Checked ? 1 : 0 }.Concat(perms).Append(_id).ToArray());
            if (_pw.Text != "") db.Exec("UPDATE users SET password_hash=?, must_change_pw=1 WHERE id=?", PasswordHasher.Hash(_pw.Text), _id);
        }
        else
            db.Exec("INSERT INTO users (username, full_name, department, role, phone, password_hash, must_change_pw, active, " + string.Join(", ", Perms.Keys) +
                    ", created_at) VALUES (?,?,?,?,?,?,1,?," + string.Join(",", Perms.Keys.Select(_ => "?")) + ",?)",
                new object?[] { username, name, _dept.Text.Trim(), role, _phone.Text.Trim(), PasswordHasher.Hash(_pw.Text), _active.Checked ? 1 : 0 }
                    .Concat(perms).Append(Db.Now()).ToArray());
        Audit.Log(db, App.User, "Kullanıcı kaydedildi", $"{username} ({role}): " + string.Join(", ", Perms.Keys.Where(k => _perms[k].Checked)));
        db.Commit();
    });
}
