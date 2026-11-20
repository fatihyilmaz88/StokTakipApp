using System.Data;
using StokTakip.Core;
using Xunit;

namespace StokTakip.Tests;

/// <summary>Her test kendi geçici veritabanını kullanır.</summary>
[Collection("db")]
public class CoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stoktest_" + Guid.NewGuid().ToString("N"));
    private readonly AppUser _admin;

    public CoreTests()
    {
        Directory.CreateDirectory(_dir);
        Db.Configure(Path.Combine(_dir, "stok.db"), Path.Combine(_dir, "uploads"));
        Assert.True(Db.Initialize());
        _admin = Auth.Login("admin", "admin123")!;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private long NewProduct(DbSession db, string code, double qty, long loc)
    {
        var id = Products.Save(db, _admin, new ProductData { Code = code, Name = code + " ürün", Unit = "adet", DefaultLocationId = loc, MinStock = 5 }, qty);
        db.Commit();
        return id;
    }

    private long NewLoc(DbSession db, string code) =>
        db.Insert("INSERT INTO locations (code, name, type, parent_id) VALUES (?,?,'Raf',1)", code, code);

    [Fact]
    public void Werkzeug_scrypt_ve_pbkdf2_hashleri_dogrulanir()
    {
        // Python werkzeug ile üretilen gerçek özetler
        Assert.True(PasswordHasher.Verify("admin123",
            "scrypt:32768:8:1$HF7lSuH6UhBhhark$8e44954bea72a1be01b950724a0fc017876714e3d122804c5cc92f06bcb2e34ccc957eb3e3bfa3edf6250238293dcfe949593770f5d078bc57eeb0c75039f9ca"));
        Assert.True(PasswordHasher.Verify("Test-Şifre1",
            "pbkdf2:sha256:1000$lVpEz3hDFV9Zyf8b$948844d8d220ab3c64c05898cc3b4ee0fe558e0c5c2c16aa29303911cedb7a96"));
        Assert.False(PasswordHasher.Verify("yanlis",
            "pbkdf2:sha256:1000$lVpEz3hDFV9Zyf8b$948844d8d220ab3c64c05898cc3b4ee0fe558e0c5c2c16aa29303911cedb7a96"));
        var h = PasswordHasher.Hash("Yeni.Şifre9");
        Assert.StartsWith("pbkdf2:sha256:600000$", h);
        Assert.True(PasswordHasher.Verify("Yeni.Şifre9", h));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "csharp_hash.txt"), h);
    }

    [Fact]
    public void Ilk_kurulumda_admin_sifre_degistirmeye_zorlanir()
    {
        Assert.NotNull(_admin);
        Assert.True(_admin.MustChangePassword);
        Assert.True(_admin.Can("can_out"));
        Assert.Null(Auth.Login("admin", "yanlis"));
        Assert.Equal("Yeni şifreler eşleşmiyor.", Auth.ChangePassword(_admin.Id, "admin123", "abcdef", "abcdeX"));
        Assert.Null(Auth.ChangePassword(_admin.Id, "admin123", "Yeni123", "Yeni123"));
        Assert.False(Auth.Login("admin", "Yeni123")!.MustChangePassword);
    }

    [Fact]
    public void Lokasyon_bazli_giris_cikis_transfer_sayim()
    {
        using var db = Db.Open();
        long a = NewLoc(db, "R1"), b = NewLoc(db, "R2");
        var pid = NewProduct(db, "HRT-1", 10, a);

        Stock.Apply(db, _admin, pid, "GIRIS", 5.5, null, a, new MoveMeta { UnitPrice = 12.5, Currency = "EUR", DocumentNo = "FTR-1" });
        db.Commit();
        Assert.Equal(15.5, db.Scalar<double>("SELECT quantity FROM products WHERE id=?", pid));
        Assert.Equal("EUR", db.Scalar<string>("SELECT currency FROM products WHERE id=?", pid));

        Stock.Apply(db, _admin, pid, "TRANSFER", 6, a, b);
        db.Commit();
        Assert.Equal(9.5, db.Scalar<double>("SELECT qty FROM stock WHERE product_id=? AND location_id=?", pid, a));
        Assert.Equal(6, db.Scalar<double>("SELECT qty FROM stock WHERE product_id=? AND location_id=?", pid, b));
        Assert.Equal(15.5, db.Scalar<double>("SELECT quantity FROM products WHERE id=?", pid));

        // b rafında 6 var, 7 çıkış yapılamaz ve hiçbir şey değişmez
        var ex = Assert.Throws<StockException>(() => Stock.Apply(db, _admin, pid, "CIKIS", 7, b, null));
        Assert.Contains("yetersiz stok", ex.Message);
        db.Rollback();
        Assert.Equal(6, db.Scalar<double>("SELECT qty FROM stock WHERE product_id=? AND location_id=?", pid, b));

        Stock.Apply(db, _admin, pid, "SAYIM", 4, null, b, new MoveMeta { Note = "Sayım" });
        db.Commit();
        Assert.Equal(13.5, db.Scalar<double>("SELECT quantity FROM products WHERE id=?", pid));
        Assert.Equal(-2, db.Scalar<double>("SELECT qty FROM movements WHERE type='SAYIM' ORDER BY id DESC LIMIT 1"));
    }

    [Fact]
    public void Talep_onay_kismi_teslim_akisi()
    {
        using var db = Db.Open();
        long a = NewLoc(db, "R1");
        var p1 = NewProduct(db, "P1", 10, a);
        var p2 = NewProduct(db, "P2", 3, a);
        db.Exec("INSERT INTO users (username, full_name, password_hash, can_request, created_at) VALUES ('ali','Ali Usta',?,1,?)", PasswordHasher.Hash("x123456"), Db.Now());
        var ali = Auth.Login("ali", "x123456")!;
        Assert.False(ali.Can("can_out"));

        var rid = Requests.Create(db, ali, "PRES-12", "", "Acil", "", new[] { (p1, 4.0, ""), (p2, 5.0, "") });
        db.Commit();
        Assert.Throws<StockException>(() => Requests.Issue(db, _admin, rid, new[] { (1L, 1.0, (long?)a) }, ""));  // onaysız
        Assert.Throws<StockException>(() => Requests.Decide(db, ali, rid, true, ""));                             // yetkisiz onay
        Requests.Decide(db, _admin, rid, true, "");
        db.Commit();

        var items = Requests.Items(db, rid).Rows.Cast<DataRow>().ToList();
        long i1 = items[0].Lng("id"), i2 = items[1].Lng("id");
        Assert.Throws<StockException>(() => Requests.Issue(db, _admin, rid, new[] { (i1, 5.0, (long?)a) }, ""));  // talepten fazla
        db.Rollback();
        var st = Requests.Issue(db, _admin, rid, new[] { (i1, 4.0, (long?)a), (i2, 3.0, (long?)a) }, "Ali Usta");
        db.Commit();
        Assert.Equal("KISMI", st);
        Assert.Equal(6, db.Scalar<double>("SELECT quantity FROM products WHERE id=?", p1));
        Assert.Equal(0, db.Scalar<double>("SELECT quantity FROM products WHERE id=?", p2));
        Assert.Equal(2, db.Scalar<long>("SELECT COUNT(*) FROM movements WHERE request_id=?", rid));
        Requests.Close(db, _admin, rid);
        db.Commit();
        Assert.Equal("TAMAMLANDI", db.Scalar<string>("SELECT status FROM requests WHERE id=?", rid));
    }

    [Fact]
    public void Csv_ice_aktarma_kategori_lokasyon_tedarikci_olusturur()
    {
        var f = Path.Combine(_dir, "u.csv");
        File.WriteAllText(f, "kod;ad;kategori;birim;lokasyon_kodu;min_stok;miktar;marka;model;tedarikci;fiyat;para\n" +
                             "HRT-012;\"Hortum 1/2\"\" 2SN\";Hidrolik/Hortum;m;A-01;30;120;Parker;492TC-8;Hidrosan;185,50;TRY\n" +
                             "CNT-018;Kontaktör;Elektrik/Kontaktör;adet;B-03;10;6;Schneider;LC1D18BD;Elektrosa;42,3;EUR\n" +
                             "HRT-012;Tekrar;;;;;;;;;;\n", new System.Text.UTF8Encoding(true));
        using var db = Db.Open();
        var (added, skipped, errors) = Products.ImportCsv(db, _admin, f);
        db.Commit();
        Assert.Equal((2, 1, 0), (added, skipped, errors.Count));
        Assert.Equal("Hortum 1/2\" 2SN", db.Scalar<string>("SELECT name FROM products WHERE code='HRT-012'"));
        Assert.Equal(185.5, db.Scalar<double>("SELECT last_price FROM products WHERE code='HRT-012'"));
        Assert.Equal("Hidrolik / Hortum", Tree.Paths(db, "categories")[db.Scalar<long>("SELECT category_id FROM products WHERE code='HRT-012'")]);
        Assert.Equal(1, Reports.Reorder(db, _admin).Rows.Count);   // kontaktör 6 < 10
        var csv = Path.Combine(_dir, "out.csv");
        Csv.Write(csv, Reports.Value(db, "kategori"));
        Assert.Contains("TOPLAM", File.ReadAllText(csv));
    }

    [Fact]
    public void Dosya_eki_kopyalanir_ve_silinir()
    {
        using var db = Db.Open();
        var pid = NewProduct(db, "V1", 0, 1);
        var src = Path.Combine(_dir, "datasheet.pdf");
        File.WriteAllText(src, "%PDF-1.4");
        var aid = Attachments.Save(db, _admin, "product", pid, src, "Datasheet");
        var stored = db.Scalar<string>("SELECT stored_name FROM attachments WHERE id=?", aid);
        Assert.True(File.Exists(Attachments.FullPath(stored)));
        Assert.Throws<StockException>(() => Attachments.Save(db, _admin, "product", pid, Path.Combine(_dir, "x.exe").Also(p => File.WriteAllText(p, "x")), "Diğer"));
        Attachments.Delete(db, _admin, aid);
        Assert.False(File.Exists(Attachments.FullPath(stored)));
    }

    [Fact]
    public void Turkce_sayi_bicimi()
    {
        Assert.Equal(1234.5, Fmt.ParseNum("1.234,5"));
        Assert.Equal(12.5, Fmt.ParseNum("12,5"));
        Assert.Equal(12.5, Fmt.ParseNum("12.5"));
        Assert.Null(Fmt.ParseNum("abc"));
        Assert.Equal("1.234,5", Fmt.Qty(1234.5));
    }
}

internal static class TestExt
{
    public static string Also(this string s, Action<string> a) { a(s); return s; }
}
