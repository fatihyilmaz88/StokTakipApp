using System.Data;

namespace StokTakip.Core;

/// <summary>Oturum açmış kullanıcı ve yetki kontrolü.</summary>
public sealed class AppUser
{
    public long Id { get; }
    public string Username { get; }
    public string FullName { get; }
    public string Department { get; }
    public string Role { get; }
    public bool MustChangePassword { get; }
    private readonly HashSet<string> _perms;

    public AppUser(DataRow r)
    {
        Id = r.Lng("id");
        Username = r.Str("username");
        FullName = r.Str("full_name");
        Department = r.Str("department");
        Role = r.Str("role");
        MustChangePassword = r.Bool("must_change_pw");
        _perms = Perms.Keys.Where(k => r.Bool(k)).ToHashSet();
    }

    public bool IsAdmin => _perms.Contains("is_admin");
    public bool Can(string perm) => IsAdmin || _perms.Contains(perm);
    public bool CanAny(params string[] perms) => perms.Any(Can);
}

public static class Auth
{
    /// <summary>Kullanıcı adı/şifre doğrular. Başarısız girişleri denetim kaydına yazar.</summary>
    public static AppUser? Login(string username, string password)
    {
        using var db = Db.Open();
        var r = db.Row("SELECT * FROM users WHERE username=?", username.Trim());
        if (r != null && r.Bool("active") && PasswordHasher.Verify(password, r.Str("password_hash")))
        {
            db.Exec("UPDATE users SET last_login=? WHERE id=?", Db.Now(), r.Lng("id"));
            return new AppUser(r);
        }
        Audit.Log(db, null, "Hatalı giriş denemesi", username);
        return null;
    }

    public static AppUser Reload(long id)
    {
        using var db = Db.Open();
        return new AppUser(db.Row("SELECT * FROM users WHERE id=?", id) ?? throw new InvalidOperationException("Kullanıcı bulunamadı"));
    }

    /// <summary>Hata mesajı döndürür; null ise başarılı.</summary>
    public static string? ChangePassword(long userId, string oldPw, string newPw, string newPw2)
    {
        using var db = Db.Open();
        var hash = db.Scalar<string>("SELECT password_hash FROM users WHERE id=?", userId);
        if (!PasswordHasher.Verify(oldPw, hash)) return "Mevcut şifre hatalı.";
        if (newPw.Length < 6) return "Yeni şifre en az 6 karakter olmalı.";
        if (newPw != newPw2) return "Yeni şifreler eşleşmiyor.";
        if (newPw == oldPw) return "Yeni şifre eskisiyle aynı olamaz.";
        db.Exec("UPDATE users SET password_hash=?, must_change_pw=0 WHERE id=?", PasswordHasher.Hash(newPw), userId);
        return null;
    }
}

public static class Audit
{
    public static void Log(DbSession db, AppUser? u, string action, string detail = "") =>
        db.Exec("INSERT INTO audit (user_id, action, detail, ip, created_at) VALUES (?,?,?,?,?)",
            u?.Id, action, detail.Length > 1000 ? detail[..1000] : detail, Environment.MachineName, Db.Now());
}
