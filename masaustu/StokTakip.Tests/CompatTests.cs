using StokTakip.Core;
using Xunit;

namespace StokTakip.Tests;

/// <summary>
/// Web (Python) sürümünün oluşturduğu gerçek bir veritabanıyla uyumluluk testi.
/// STOK_UYUM_DB ortam değişkeni bir stok.db yolunu gösterirse çalışır, yoksa atlanır.
/// </summary>
[Collection("db")]
public class CompatTests
{
    [Fact]
    public void Web_surumu_veritabanini_acar_ve_islem_yapar()
    {
        var path = Environment.GetEnvironmentVariable("STOK_UYUM_DB");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        Db.Configure(path, Path.Combine(Path.GetDirectoryName(path)!, "uploads"));
        Assert.False(Db.Initialize());                                   // mevcut veritabanı, yeniden kurulum yok
        var ambar = Auth.Login("ambar", "Ambar123");                     // web'de scrypt ile oluşturulmuş şifre
        Assert.NotNull(ambar);
        Assert.False(ambar!.Can("can_see_price"));
        using var db = Db.Open();
        var p = Stock.FindProduct(db, "TC-K01")!;
        var before = p.Dbl("quantity");
        var loc = Stock.Stocks(db, p.Lng("id"), true)[0].Id;
        Stock.Apply(db, ambar, p.Lng("id"), "CIKIS", 1, loc, null, new MoveMeta { Project = "MASAÜSTÜ-TEST", Receiver = "Test" });
        db.Commit();
        Assert.Equal(before - 1, db.Scalar<double>("SELECT quantity FROM products WHERE id=?", p.Lng("id")));
        Assert.True(Reports.Consumption(db, ambar, "2000-01-01", "2100-01-01", "proje").Rows.Count > 0);
        Assert.True(Requests.Items(db, 1).Rows.Count == 3);
    }
}
