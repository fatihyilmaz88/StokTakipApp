using System.Data;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;

namespace StokTakip.Core;

/// <summary>Veritabanı ayarları ve oturum açma. Şema web sürümüyle birebir aynıdır.</summary>
public static class Db
{
    public static string DbPath { get; private set; } = "";
    public static string UploadDir { get; private set; } = "";

    public static void Configure(string dbPath, string uploadDir)
    {
        DbPath = dbPath;
        UploadDir = uploadDir;
    }

    public static DbSession Open()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            ForeignKeys = true,
            DefaultTimeout = 15,
            Pooling = true,
        }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        return new DbSession(c);
    }

    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Tabloları oluşturur, ilk yönetici ve ana depo kaydını ekler. true: ilk kurulum yapıldı.</summary>
    public static bool Initialize()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(DbPath))!);
        Directory.CreateDirectory(UploadDir);
        using var db = Open();
        db.Exec("PRAGMA journal_mode=WAL");
        var cols = db.Query("PRAGMA table_info(products)").Rows.Cast<DataRow>().Select(r => r.Str("name")).ToList();
        if (cols.Count > 0 && !cols.Contains("brand"))
            throw new InvalidOperationException("Veritabanı eski sürüme (v1) ait. Yedekleyip farklı bir dosya seçin.");
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("StokTakip.Core.Schema.sql")!)
        using (var rd = new StreamReader(s))
            db.ExecScript(rd.ReadToEnd());
        bool first = false;
        if (db.Scalar<long>("SELECT COUNT(*) FROM users") == 0)
        {
            db.Exec("INSERT INTO users (username, full_name, role, password_hash, must_change_pw, " +
                    string.Join(", ", Perms.Keys) + ", created_at) VALUES (?,?,?,?,1," +
                    string.Join(",", Perms.Keys.Select(_ => "1")) + ",?)",
                "admin", "Sistem Yöneticisi", "Sistem Yöneticisi", PasswordHasher.Hash("admin123"), Now());
            first = true;
        }
        if (db.Scalar<long>("SELECT COUNT(*) FROM locations") == 0)
            db.Exec("INSERT INTO locations (code, name, type) VALUES ('ANA','Ana Ambar','Depo')");
        return first;
    }
}

/// <summary>Bir bağlantı + (varsa) aktif işlem. Komutlar işleme otomatik bağlanır.</summary>
public sealed class DbSession : IDisposable
{
    public SqliteConnection Conn { get; }
    private SqliteTransaction? _tx;

    internal DbSession(SqliteConnection c) => Conn = c;

    public bool InTransaction => _tx != null;

    /// <summary>Yazma kilidiyle (BEGIN IMMEDIATE) işlem başlatır.</summary>
    public void Begin()
    {
        if (_tx == null) _tx = Conn.BeginTransaction(deferred: false);
    }

    public void Commit() { _tx?.Commit(); _tx?.Dispose(); _tx = null; }
    public void Rollback() { _tx?.Rollback(); _tx?.Dispose(); _tx = null; }

    /// <summary>"?" yer tutucularını @p0, @p1… parametrelerine çevirir (tırnak içleri hariç).</summary>
    private SqliteCommand Cmd(string sql, object?[] args)
    {
        var sb = new StringBuilder();
        int n = 0;
        bool inStr = false;
        foreach (var ch in sql)
        {
            if (ch == '\'') inStr = !inStr;
            if (ch == '?' && !inStr) sb.Append("@p").Append(n++);
            else sb.Append(ch);
        }
        var cmd = Conn.CreateCommand();
        cmd.CommandText = sb.ToString();
        cmd.Transaction = _tx;
        for (int i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("@p" + i, args[i] ?? DBNull.Value);
        return cmd;
    }

    public int Exec(string sql, params object?[] args)
    {
        using var c = Cmd(sql, args);
        return c.ExecuteNonQuery();
    }

    public long Insert(string sql, params object?[] args)
    {
        using var c = Cmd(sql + "; SELECT last_insert_rowid();", args);
        return Convert.ToInt64(c.ExecuteScalar());
    }

    public void ExecScript(string sql)
    {
        using var c = Conn.CreateCommand();
        c.CommandText = sql;
        c.Transaction = _tx;
        c.ExecuteNonQuery();
    }

    public T Scalar<T>(string sql, params object?[] args)
    {
        using var c = Cmd(sql, args);
        var v = c.ExecuteScalar();
        if (v == null || v is DBNull) return default!;
        var t = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(v, t);
    }

    /// <summary>Sorgu sonucunu DataTable olarak döndürür (DataGridView'e doğrudan bağlanabilir).</summary>
    public DataTable Query(string sql, params object?[] args)
    {
        using var c = Cmd(sql, args);
        using var r = c.ExecuteReader();
        var dt = new DataTable();
        for (int i = 0; i < r.FieldCount; i++)
        {
            var name = r.GetName(i);
            while (dt.Columns.Contains(name)) name += "_";
            dt.Columns.Add(name, typeof(object));
        }
        while (r.Read())
        {
            var vals = new object[r.FieldCount];
            r.GetValues(vals);
            dt.Rows.Add(vals);
        }
        return dt;
    }

    public DataRow? Row(string sql, params object?[] args)
    {
        var t = Query(sql, args);
        return t.Rows.Count > 0 ? t.Rows[0] : null;
    }

    public void Dispose()
    {
        if (_tx != null) Rollback();
        Conn.Dispose();
    }
}

public static class RowExt
{
    public static string Str(this DataRow r, string c) =>
        r.Table.Columns.Contains(c) && r[c] is not DBNull ? Convert.ToString(r[c]) ?? "" : "";
    public static double Dbl(this DataRow r, string c) =>
        r.Table.Columns.Contains(c) && r[c] is not DBNull ? Convert.ToDouble(r[c]) : 0;
    public static long Lng(this DataRow r, string c) =>
        r.Table.Columns.Contains(c) && r[c] is not DBNull ? Convert.ToInt64(r[c]) : 0;
    public static long? NLng(this DataRow r, string c) =>
        r.Table.Columns.Contains(c) && r[c] is not DBNull ? Convert.ToInt64(r[c]) : null;
    public static double? NDbl(this DataRow r, string c) =>
        r.Table.Columns.Contains(c) && r[c] is not DBNull ? Convert.ToDouble(r[c]) : null;
    public static bool Bool(this DataRow r, string c) => r.Lng(c) != 0;
}
