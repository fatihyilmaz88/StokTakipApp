using StokTakip.Core;
using StokTakip.Masaustu.Forms;

namespace StokTakip.Masaustu;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Ui.Error("Beklenmeyen hata:\n" + e.Exception.Message);

        while (true)
        {
            var s = Settings.Load();
            Db.Configure(s.DbPath, s.UploadDir);
            try
            {
                if (Db.Initialize())
                    Ui.Info("Veritabanı oluşturuldu:\n" + s.DbPath + "\n\nİlk giriş: admin / admin123\n(İlk girişte şifrenizi değiştirmeniz istenecek.)");
            }
            catch (Exception ex)
            {
                Ui.Error("Veritabanı açılamadı:\n" + ex.Message + "\n\nAyarlardan farklı bir dosya seçin.");
                using var sf = new SettingsForm();
                if (sf.ShowDialog() != DialogResult.OK) return;
                continue;
            }

            using var login = new LoginForm();
            var res = login.ShowDialog();
            if (res == DialogResult.Retry) continue;          // ayarlar değişti, yeniden başlat
            if (res != DialogResult.OK || login.User == null) return;

            App.User = login.User;
            if (App.User.MustChangePassword)
            {
                using var cp = new ChangePasswordForm(true);
                if (cp.ShowDialog() != DialogResult.OK) return;
                App.User = Auth.Reload(App.User.Id);
            }
            using var main = new MainForm();
            Application.Run(main);
            Backup.AutoDaily();
            if (!main.LogoutRequested) return;                // "Oturumu Kapat" ile tekrar giriş ekranı
        }
    }
}

/// <summary>Oturum bilgisi.</summary>
internal static class App
{
    public static AppUser User = null!;
    public static string Company => Settings.Load().Company;
}
