using System.Data;
using StokTakip.Core;

namespace StokTakip.Masaustu.Forms;

public class DashboardPage : UserControl, IPage
{
    private readonly MainForm _main;
    private readonly FlowLayoutPanel _tiles = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
    private readonly DataGridView _low = Ui.Grid(), _recent = Ui.Grid(), _mine = Ui.Grid();

    public DashboardPage(MainForm main)
    {
        _main = main;
        var u = App.User;
        var actions = new List<Control>();
        if (u.Can("can_in")) actions.Add(Ui.Btn("＋ Stok Girişi", (_, _) => _main.OpenMovement("GIRIS"), Ui.InGreen));
        if (u.Can("can_out")) actions.Add(Ui.Btn("－ Stok Çıkışı", (_, _) => _main.OpenMovement("CIKIS"), Ui.OutRed));
        if (u.Can("can_transfer")) actions.Add(Ui.Btn("⇄ Transfer", (_, _) => _main.OpenMovement("TRANSFER")));
        if (u.Can("can_request")) actions.Add(Ui.Btn("📝 Malzeme Talebi", (_, _) => { using var f = new RequestNewForm(); if (f.ShowDialog() == DialogResult.OK) RefreshData(); }, Ui.Primary));
        if (u.Can("can_edit")) actions.Add(Ui.Btn("Yeni Ürün", (_, _) => { using var f = new ProductEditForm(null); if (f.ShowDialog() == DialogResult.OK) RefreshData(); }));

        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        split.Controls.Add(Box("⚠ Kritik Stoktaki Ürünler", _low), 0, 0);
        split.Controls.Add(Box("Açık Taleplerim", _mine), 1, 0);
        var rec = Box("Son Hareketler", _recent);
        split.Controls.Add(rec, 0, 1);
        split.SetColumnSpan(rec, 2);

        _low.RowColors(r => r.Dbl("Mevcut") <= 0 ? Ui.Danger : Ui.Warn);
        _low.CellDoubleClick += (_, _) => { if (_low.SelId() is long id) { ProductDetailForm.Open(id); RefreshData(); } };
        _mine.CellDoubleClick += (_, _) => { if (_mine.SelId() is long id) { using var f = new RequestDetailForm(id); f.ShowDialog(); RefreshData(); } };
        _recent.CellDoubleClick += (_, _) => { if (_recent.SelId("product_id") is long id) { ProductDetailForm.Open(id); RefreshData(); } };

        Controls.Add(split);
        Controls.Add(Ui.Bar(actions.ToArray()));
        Controls.Add(_tiles);
        Controls.Add(Ui.Title($"Merhaba {u.FullName}  ·  {u.Role}"));
    }

    internal static Control Box(string title, Control inner)
    {
        var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), Margin = new Padding(0, 0, 10, 10), BackColor = Color.White };
        p.Controls.Add(inner);
        p.Controls.Add(new Label { Text = title, Font = Ui.H2, Dock = DockStyle.Top, Height = 30, ForeColor = Ui.Primary });
        return p;
    }

    private Control Tile(string value, string label, Color back, Action? click = null)
    {
        var p = new Panel { Width = 200, Height = 82, BackColor = back, Margin = new Padding(0, 0, 10, 10), Cursor = click != null ? Cursors.Hand : Cursors.Default };
        var v = new Label { Text = value, Font = value.Length > 10 ? Ui.H2 : Ui.Big, AutoSize = true, Left = 12, Top = 8 };
        var l = new Label { Text = label, ForeColor = Color.DimGray, AutoSize = true, Left = 14, Top = 54 };
        p.Controls.AddRange(new Control[] { v, l });
        if (click != null) foreach (Control c in new Control[] { p, v, l }) c.Click += (_, _) => click();
        return p;
    }

    public void RefreshData()
    {
        using var db = Db.Open();
        var u = App.User;
        var st = db.Row("SELECT COUNT(*) total, SUM(CASE WHEN quantity<=min_stock AND min_stock>0 THEN 1 ELSE 0 END) low, " +
                        "SUM(CASE WHEN quantity<=0 THEN 1 ELSE 0 END) empty FROM products WHERE active=1")!;
        var today = db.Query("SELECT type, COUNT(*) c FROM movements WHERE created_at >= date('now','localtime') GROUP BY type")
            .Rows.Cast<DataRow>().ToDictionary(r => r.Str("type"), r => r.Lng("c"));
        _tiles.Controls.Clear();
        _tiles.Controls.Add(Tile(st.Lng("total").ToString(), "Aktif Ürün", Color.White, () => _main.ShowPage("Ürünler")));
        _tiles.Controls.Add(Tile(st.Lng("low").ToString(), "Kritik Seviyede", Ui.Warn));
        _tiles.Controls.Add(Tile(st.Lng("empty").ToString(), "Stoğu Bitmiş", Ui.Danger));
        _tiles.Controls.Add(Tile($"{today.GetValueOrDefault("GIRIS")} / {today.GetValueOrDefault("CIKIS")}", "Bugün Giriş / Çıkış", Color.White));
        if (u.Can("can_approve"))
        {
            var n = db.Scalar<long>("SELECT COUNT(*) FROM requests WHERE status='BEKLIYOR'");
            if (n > 0) _tiles.Controls.Add(Tile(n.ToString(), "Onayınızı Bekleyen Talep", Ui.Soft, () => _main.ShowPage("Malzeme Talepleri")));
        }
        if (u.Can("can_out"))
        {
            var n = db.Scalar<long>("SELECT COUNT(*) FROM requests WHERE status IN ('ONAYLANDI','KISMI')");
            if (n > 0) _tiles.Controls.Add(Tile(n.ToString(), "Ambardan Teslim Bekleyen", Ui.Soft, () => _main.ShowPage("Malzeme Talepleri")));
        }
        if (u.Can("can_see_price"))
            foreach (DataRow r in db.Query("SELECT currency, SUM(quantity*last_price) v FROM products WHERE active=1 AND quantity>0 GROUP BY currency").Rows)
                _tiles.Controls.Add(Tile(Fmt.Money(r.Dbl("v"), r.Str("currency")), $"Stok Değeri ({r.Str("currency")})", Color.White));

        _low.Bind(db.Query("SELECT p.id, p.code AS Kod, p.name AS Ürün, p.quantity AS Mevcut, p.min_stock AS \"Min.\", p.unit AS Birim, " +
                           "s.name AS Tedarikçi FROM products p LEFT JOIN suppliers s ON s.id=p.default_supplier_id " +
                           "WHERE p.active=1 AND p.min_stock>0 AND p.quantity<=p.min_stock ORDER BY p.quantity/p.min_stock LIMIT 50"));
        _mine.Bind(db.Query("SELECT id, 'MT-'||substr(created_at,1,4)||'-'||printf('%05d',id) AS No, project AS \"Proje/Makine\", " +
                            "CASE status WHEN 'BEKLIYOR' THEN 'Onay Bekliyor' WHEN 'ONAYLANDI' THEN 'Onaylandı – Ambarda' ELSE 'Kısmen Teslim' END AS Durum " +
                            "FROM requests WHERE requester_id=? AND status IN ('BEKLIYOR','ONAYLANDI','KISMI') ORDER BY id DESC", u.Id));
        _recent.Bind(MovementsPage.Shape(db, db.Query(Stock.MoveSelect + " ORDER BY m.id DESC LIMIT 20")));
    }
}
