using StokTakip.Core;

namespace StokTakip.Masaustu.Forms;

public class LoginForm : Form
{
    public AppUser? User { get; private set; }
    private readonly TextBox _user = new() { Width = 300, Font = Ui.Base };
    private readonly TextBox _pw = new() { Width = 300, Font = Ui.Base, UseSystemPasswordChar = true };

    public LoginForm()
    {
        Text = "Stok Takip – Giriş"; Font = Ui.Base; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; ClientSize = new Size(380, 330); BackColor = Color.White;
        var head = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = Ui.Primary };
        head.Controls.Add(new Label { Text = "📦 " + App.Company, Font = Ui.H1, ForeColor = Color.White, AutoSize = true, Left = 20, Top = 12 });
        head.Controls.Add(new Label { Text = "Stok ve Ambar Yönetimi", ForeColor = Color.White, AutoSize = true, Left = 22, Top = 48 });
        var login = Ui.Btn("Giriş", (_, _) => DoLogin(), Ui.Primary, 300);
        var settings = new LinkLabel { Text = "Ayarlar (veritabanı yeri, firma adı)", AutoSize = true, Left = 40, Top = 290 };
        settings.Click += (_, _) =>
        {
            using var f = new SettingsForm();
            if (f.ShowDialog(this) == DialogResult.OK) { DialogResult = DialogResult.Retry; Close(); }
        };
        _user.SetBounds(40, 125, 300, 27);
        _pw.SetBounds(40, 185, 300, 27);
        login.SetBounds(40, 230, 300, 40);
        Controls.AddRange(new Control[]
        {
            head, new Label { Text = "Kullanıcı Adı", Left = 40, Top = 102, AutoSize = true }, _user,
            new Label { Text = "Şifre", Left = 40, Top = 162, AutoSize = true }, _pw, login, settings,
        });
        AcceptButton = login;
    }

    private void DoLogin()
    {
        User = Auth.Login(_user.Text, _pw.Text);
        if (User == null) { Ui.Error("Kullanıcı adı veya şifre hatalı ya da hesap pasif."); _pw.SelectAll(); _pw.Focus(); return; }
        DialogResult = DialogResult.OK;
        Close();
    }
}

public class ChangePasswordForm : DialogBase
{
    private readonly TextBox _old = new() { UseSystemPasswordChar = true }, _new = new() { UseSystemPasswordChar = true },
        _new2 = new() { UseSystemPasswordChar = true };

    public ChangePasswordForm(bool forced) : base("Şifre Değiştir", 480, 290)
    {
        var f = Ui.Fields();
        if (forced) Body.Controls.Add(new Label { Text = "Devam etmeden önce şifrenizi değiştirmeniz gerekiyor.", Dock = DockStyle.Top, Height = 26, ForeColor = Ui.OutRed });
        f.Add("Mevcut şifre", _old);
        f.Add("Yeni şifre (en az 6)", _new);
        f.Add("Yeni şifre (tekrar)", _new2);
        Body.Controls.Add(f);
        f.BringToFront();
        AcceptButton = OkButton;
    }

    protected override bool OnSave()
    {
        var err = Auth.ChangePassword(App.User.Id, _old.Text, _new.Text, _new2.Text);
        if (err != null) { Ui.Error(err); return false; }
        Ui.Info("Şifreniz değiştirildi.");
        return true;
    }
}

public class SettingsForm : DialogBase
{
    private readonly Settings _s = Settings.Load();
    private readonly TextBox _db, _up, _bk, _co;

    public SettingsForm() : base("Ayarlar", 720, 380)
    {
        var f = Ui.Fields();
        f.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        f.ColumnCount = 3;
        _co = Ui.Text(_s.Company); _db = Ui.Text(_s.DbPath); _up = Ui.Text(_s.UploadDir); _bk = Ui.Text(_s.BackupDir);
        f.Add("Firma adı", _co);
        f.Add("Veritabanı dosyası", _db);
        f.Controls.Add(Ui.Btn("Seç…", (_, _) =>
        {
            using var d = new SaveFileDialog { Filter = "SQLite (*.db)|*.db", FileName = Path.GetFileName(_db.Text), OverwritePrompt = false, CheckFileExists = false };
            if (d.ShowDialog() == DialogResult.OK) _db.Text = d.FileName;
        }), 2, f.RowCount - 1);
        f.Add("Dosya ekleri klasörü", _up);
        f.Controls.Add(Ui.Btn("Seç…", (_, _) => PickDir(_up)), 2, f.RowCount - 1);
        f.Add("Yedek klasörü", _bk);
        f.Controls.Add(Ui.Btn("Seç…", (_, _) => PickDir(_bk)), 2, f.RowCount - 1);
        Body.Controls.Add(new Label
        {
            Dock = DockStyle.Top, Height = 64, ForeColor = Color.DimGray,
            Text = "Web (sunucu) sürümüne geçerken bu veritabanını ve ekler klasörünü sunucuya kopyalamanız yeterlidir; " +
                   "iki sürüm aynı dosya yapısını kullanır.\nAyarlar kaydedilince program yeniden başlar.",
        });
        Body.Controls.Add(f);
        f.BringToFront();
    }

    private static void PickDir(TextBox t)
    {
        using var d = new FolderBrowserDialog { SelectedPath = t.Text };
        if (d.ShowDialog() == DialogResult.OK) t.Text = d.SelectedPath;
    }

    protected override bool OnSave()
    {
        if (string.IsNullOrWhiteSpace(_db.Text) || string.IsNullOrWhiteSpace(_up.Text)) { Ui.Error("Veritabanı ve ekler klasörü boş olamaz."); return false; }
        _s.Company = _co.Text.Trim(); _s.DbPath = _db.Text.Trim(); _s.UploadDir = _up.Text.Trim(); _s.BackupDir = _bk.Text.Trim();
        _s.Save();
        return true;
    }
}
