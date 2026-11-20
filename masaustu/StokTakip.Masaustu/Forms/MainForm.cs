using StokTakip.Core;

namespace StokTakip.Masaustu.Forms;

/// <summary>Sayfalar bu arayüzü uygular; ana pencere sayfa değişince/geri dönünce yeniler.</summary>
public interface IPage
{
    void RefreshData();
}

public class MainForm : Form
{
    public bool LogoutRequested { get; private set; }
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 14), BackColor = Ui.Back };
    private readonly FlowLayoutPanel _nav = new()
    {
        Dock = DockStyle.Left, Width = 210, FlowDirection = FlowDirection.TopDown, WrapContents = false,
        BackColor = Ui.Primary, Padding = new Padding(0, 6, 0, 0), AutoScroll = true,
    };
    private readonly Dictionary<string, Func<Control>> _pages = new();
    private readonly Dictionary<string, Button> _navButtons = new();
    private readonly System.Windows.Forms.Timer _badgeTimer = new() { Interval = 60_000 };
    private string _current = "";

    public MainForm()
    {
        var u = App.User;
        Text = $"{App.Company} – Stok Takip   |   {u.FullName} ({u.Role})";
        Font = Ui.Base; WindowState = FormWindowState.Maximized; MinimumSize = new Size(1000, 650); KeyPreview = true;

        var brand = new Label { Text = "📦 " + App.Company, ForeColor = Color.White, Font = Ui.H2, Width = 205, Height = 46, TextAlign = ContentAlignment.MiddleCenter };
        _nav.Controls.Add(brand);
        AddPage("Ana Sayfa", () => new DashboardPage(this));
        AddPage("Ürünler", () => new ProductsPage());
        if (u.CanAny("can_report", "can_in", "can_out")) AddPage("Stok Hareketleri", () => new MovementsPage());
        if (u.CanAny("can_request", "can_approve", "can_out")) AddPage("Malzeme Talepleri", () => new RequestsPage());
        AddPage("Lokasyonlar", () => new LocationsPage());
        if (u.CanAny("can_purchase", "can_in", "can_see_price", "can_report")) AddPage("Tedarikçiler", () => new SuppliersPage());
        if (u.CanAny("can_report", "can_see_price")) AddPage("Raporlar", () => new ReportsPage());
        if (u.IsAdmin) AddPage("Kullanıcılar", () => new UsersPage());

        _nav.Controls.Add(new Label { Height = 16, Width = 200 });
        if (u.Can("can_in")) NavAction("＋ Stok Girişi  (F2)", () => OpenMovement("GIRIS"));
        if (u.Can("can_out")) NavAction("－ Stok Çıkışı  (F3)", () => OpenMovement("CIKIS"));
        if (u.Can("can_transfer")) NavAction("⇄ Transfer  (F4)", () => OpenMovement("TRANSFER"));
        if (u.Can("can_request")) NavAction("📝 Yeni Talep  (F6)", () => { using var f = new RequestNewForm(); if (f.ShowDialog(this) == DialogResult.OK) RefreshCurrent(); });
        _nav.Controls.Add(new Label { Height = 16, Width = 200 });
        if (u.IsAdmin)
        {
            NavAction("💾 Yedek Al", () =>
            {
                using var d = new SaveFileDialog { Filter = "SQLite (*.db)|*.db", FileName = $"stok_yedek_{DateTime.Now:yyyyMMdd_HHmm}.db" };
                if (d.ShowDialog() == DialogResult.OK && Ui.Run(() => Backup.Run(d.FileName))) Ui.Info("Yedek alındı:\n" + d.FileName);
            });
            NavAction("⚙ Ayarlar", () => { using var f = new SettingsForm(); if (f.ShowDialog(this) == DialogResult.OK) { Ui.Info("Ayarlar kaydedildi, oturum yeniden açılacak."); LogoutRequested = true; Close(); } });
        }
        NavAction("🔑 Şifre Değiştir", () => { using var f = new ChangePasswordForm(false); f.ShowDialog(this); });
        NavAction("⎋ Oturumu Kapat", () => { LogoutRequested = true; Close(); });

        Controls.Add(_content);
        Controls.Add(_nav);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F2 && u.Can("can_in")) OpenMovement("GIRIS");
            else if (e.KeyCode == Keys.F3 && u.Can("can_out")) OpenMovement("CIKIS");
            else if (e.KeyCode == Keys.F4 && u.Can("can_transfer")) OpenMovement("TRANSFER");
            else if (e.KeyCode == Keys.F5) RefreshCurrent();
            else if (e.KeyCode == Keys.F6 && u.Can("can_request")) { using var f = new RequestNewForm(); if (f.ShowDialog(this) == DialogResult.OK) RefreshCurrent(); }
        };
        _badgeTimer.Tick += (_, _) => UpdateBadges();
        _badgeTimer.Start();
        Shown += (_, _) => { ShowPage("Ana Sayfa"); UpdateBadges(); };
    }

    private void AddPage(string name, Func<Control> factory)
    {
        _pages[name] = factory;
        var b = NavButton(name, () => ShowPage(name));
        _navButtons[name] = b;
    }

    private Button NavButton(string text, Action a)
    {
        var b = new Button
        {
            Text = "   " + text, Width = 205, Height = 40, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Ui.Primary,
            TextAlign = ContentAlignment.MiddleLeft, Font = Ui.Base, Margin = new Padding(0), Cursor = Cursors.Hand, Tag = text,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 98, 145);
        b.Click += (_, _) => a();
        _nav.Controls.Add(b);
        return b;
    }

    private void NavAction(string text, Action a) => NavButton(text, a).ForeColor = Color.FromArgb(220, 232, 245);

    public void ShowPage(string page)
    {
        if (!_pages.TryGetValue(page, out var f)) return;
        Cursor = Cursors.WaitCursor;
        try
        {
            _content.SuspendLayout();
            foreach (Control c in _content.Controls) c.Dispose();
            _content.Controls.Clear();
            var ctl = f();
            ctl.Dock = DockStyle.Fill;
            _content.Controls.Add(ctl);
            _content.ResumeLayout();
            _current = page;
            foreach (var (n, b) in _navButtons) b.BackColor = n == page ? Color.FromArgb(18, 52, 84) : Ui.Primary;
            if (ctl is IPage p) p.RefreshData();
        }
        finally { Cursor = Cursors.Default; }
    }

    public void RefreshCurrent()
    {
        if (_content.Controls.Count > 0 && _content.Controls[0] is IPage p) p.RefreshData();
        UpdateBadges();
    }

    public void OpenMovement(string kind, long? pid = null, long? toLoc = null)
    {
        using var f = new MovementForm(kind, pid, toLoc);
        f.ShowDialog(this);
        if (f.Saved) RefreshCurrent();
    }

    private void UpdateBadges()
    {
        if (!_navButtons.TryGetValue("Malzeme Talepleri", out var b)) return;
        try
        {
            using var db = Db.Open();
            long n = 0;
            if (App.User.Can("can_approve")) n += db.Scalar<long>("SELECT COUNT(*) FROM requests WHERE status='BEKLIYOR'");
            if (App.User.Can("can_out")) n += db.Scalar<long>("SELECT COUNT(*) FROM requests WHERE status IN ('ONAYLANDI','KISMI')");
            b.Text = "   Malzeme Talepleri" + (n > 0 ? $"   ({n})" : "");
        }
        catch { /* rozet hatası önemsiz */ }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _badgeTimer.Stop();
        base.OnFormClosed(e);
    }
}
