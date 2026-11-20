using System.Text.Json;
using Microsoft.Data.Sqlite;
using StokTakip.Core;

namespace StokTakip.Masaustu;

/// <summary>
/// Ayarlar %ProgramData%\StokTakip\ayarlar.json dosyasında tutulur (bilgisayardaki tüm Windows kullanıcıları için ortak).
/// Veritabanı varsayılan olarak aynı klasördedir.
/// </summary>
public class Settings
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StokTakip");
    private static string FilePath => Path.Combine(Folder, "ayarlar.json");

    public string DbPath { get; set; } = Path.Combine(Folder, "stok.db");
    public string UploadDir { get; set; } = Path.Combine(Folder, "uploads");
    public string BackupDir { get; set; } = Path.Combine(Folder, "yedek");
    public string Company { get; set; } = "Fabrika Ambar";
    public string LastBackup { get; set; } = "";

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* bozuk dosya: varsayılanlar */ }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>SQLite çevrimiçi yedek (program açıkken de güvenli).</summary>
public static class Backup
{
    public static string Run(string? target = null)
    {
        var s = Settings.Load();
        target ??= Path.Combine(s.BackupDir, $"stok_{DateTime.Now:yyyyMMdd_HHmm}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (var db = Db.Open())
        using (var dest = new SqliteConnection($"Data Source={target};Pooling=False"))
        {
            dest.Open();
            db.Conn.BackupDatabase(dest);
        }
        s.LastBackup = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        s.Save();
        return target;
    }

    /// <summary>Program kapanırken günde bir kez otomatik yedek; 60 günden eskileri siler.</summary>
    public static void AutoDaily()
    {
        try
        {
            var s = Settings.Load();
            if (s.LastBackup.StartsWith(DateTime.Now.ToString("yyyy-MM-dd"))) return;
            Run();
            foreach (var f in Directory.GetFiles(s.BackupDir, "stok_*.db"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-60)) File.Delete(f);
        }
        catch { /* yedek hatası programı kapatmayı engellemesin */ }
    }
}
