using System.Data;
using System.Diagnostics;
using StokTakip.Core;

namespace StokTakip.Masaustu;

/// <summary>Açılır listelerde kullanılan (id, metin) öğesi.</summary>
public sealed class Item
{
    public long? Id { get; set; }
    public string Text { get; set; } = "";
    public Item() { }
    public Item(long? id, string text) { Id = id; Text = text; }
    public override string ToString() => Text;
}

/// <summary>Ortak görünüm ve küçük yardımcılar. Formlar kodla oluşturulur; tasarımcı dosyası yoktur.</summary>
public static class Ui
{
    public static readonly Color Primary = Color.FromArgb(31, 78, 121);
    public static readonly Color InGreen = Color.FromArgb(30, 138, 74);
    public static readonly Color OutRed = Color.FromArgb(192, 57, 43);
    public static readonly Color Warn = Color.FromArgb(255, 244, 214);
    public static readonly Color Danger = Color.FromArgb(253, 227, 225);
    public static readonly Color Soft = Color.FromArgb(227, 238, 250);
    public static readonly Color Back = Color.FromArgb(242, 244, 247);
    public static readonly Font Base = new("Segoe UI", 10f);
    public static readonly Font Bold = new("Segoe UI", 10f, FontStyle.Bold);
    public static readonly Font H1 = new("Segoe UI Semibold", 16f);
    public static readonly Font H2 = new("Segoe UI Semibold", 12f);
    public static readonly Font Big = new("Segoe UI", 22f, FontStyle.Bold);

    // ------------------------------------------------------------ mesajlar
    public static void Info(string m) => MessageBox.Show(m, "Stok Takip", MessageBoxButtons.OK, MessageBoxIcon.Information);
    public static void Error(string m) => MessageBox.Show(m, "Stok Takip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    public static bool Confirm(string m) =>
        MessageBox.Show(m, "Onay", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    /// <summary>İşlemi çalıştırır; iş kuralı hatalarını kullanıcıya gösterir. Başarılıysa true.</summary>
    public static bool Run(Action a)
    {
        try { a(); return true; }
        catch (StockException ex) { Error(ex.Message); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 5)
        { Error("Veritabanı şu an meşgul. Birkaç saniye sonra tekrar deneyin."); }
        catch (Exception ex) { Error("Hata: " + ex.Message); }
        return false;
    }

    public static string? Prompt(string title, string label, string value = "")
    {
        using var f = new Form { Text = title, Width = 460, Height = 170, FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, Font = Base };
        var tb = new TextBox { Left = 12, Top = 36, Width = 420, Text = value };
        var ok = new Button { Text = "Tamam", DialogResult = DialogResult.OK, Left = 252, Top = 74, Width = 85 };
        var cancel = new Button { Text = "İptal", DialogResult = DialogResult.Cancel, Left = 347, Top = 74, Width = 85 };
        f.Controls.AddRange(new Control[] { new Label { Text = label, Left = 12, Top = 10, AutoSize = true }, tb, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
    }

    public static void OpenFile(string path)
    {
        if (!File.Exists(path)) { Error("Dosya bulunamadı:\n" + path); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static string? SaveCsvDialog(string name)
    {
        using var d = new SaveFileDialog { Filter = "Excel CSV (*.csv)|*.csv", FileName = $"{name}_{DateTime.Now:yyyyMMdd_HHmm}.csv" };
        return d.ShowDialog() == DialogResult.OK ? d.FileName : null;
    }

    public static void ExportCsv(DataTable t, string name, params string[] skip)
    {
        var f = SaveCsvDialog(name);
        if (f == null) return;
        if (Run(() => Csv.Write(f, t, skip)) && Confirm("Dosya kaydedildi. Açılsın mı?")) OpenFile(f);
    }

    // ------------------------------------------------------------ kontroller
    public static Button Btn(string text, EventHandler? click, Color? back = null, int width = 0)
    {
        var b = new Button
        {
            Text = text, AutoSize = width == 0, Width = width == 0 ? 75 : width, Height = 34, Font = Base,
            FlatStyle = FlatStyle.Flat, BackColor = back ?? Color.White, ForeColor = back == null ? Color.Black : Color.White,
            Margin = new Padding(0, 0, 6, 6), Padding = new Padding(8, 0, 8, 0), Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = back ?? Color.FromArgb(201, 208, 216);
        if (click != null) b.Click += click;
        return b;
    }

    public static FlowLayoutPanel Bar(params Control[] items)
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 4, 0, 4) };
        p.Controls.AddRange(items);
        return p;
    }

    public static Label Title(string t, Font? f = null) =>
        new() { Text = t, Font = f ?? H1, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8), ForeColor = Primary };

    public static Label Lbl(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(0, 9, 4, 0) };

    public static ComboBox Combo(IEnumerable<Item> items, int width = 220, bool editable = false)
    {
        var c = new ComboBox { Width = width, DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList, Font = Base };
        c.Items.AddRange(items.Cast<object>().ToArray());
        if (c.Items.Count > 0 && !editable) c.SelectedIndex = 0;
        return c;
    }

    public static ComboBox Combo(IEnumerable<KeyValuePair<long, string>> items, string empty, int width = 260) =>
        Combo(new[] { new Item(null, empty) }.Concat(items.Select(kv => new Item(kv.Key, kv.Value))), width);

    public static long? SelId(this ComboBox c) => (c.SelectedItem as Item)?.Id;

    public static void SelectId(this ComboBox c, long? id)
    {
        for (int i = 0; i < c.Items.Count; i++)
            if (c.Items[i] is Item it && it.Id == id) { c.SelectedIndex = i; return; }
    }

    public static TextBox Text(string v = "", bool multi = false, int width = 0) =>
        new() { Text = v, Multiline = multi, Height = multi ? 70 : 27, ScrollBars = multi ? ScrollBars.Vertical : ScrollBars.None,
                Dock = width == 0 ? DockStyle.Fill : DockStyle.None, Width = width == 0 ? 100 : width, Font = Base };

    // ------------------------------------------------------------ tablo
    public static DataGridView Grid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None, Font = Base, EnableHeadersVisualStyles = false,
            AlternatingRowsDefaultCellStyle = { BackColor = Color.FromArgb(248, 250, 252) },
            ColumnHeadersDefaultCellStyle = { BackColor = Color.FromArgb(236, 240, 244), Font = Bold },
        };
        g.RowTemplate.Height = 28;
        g.CellFormatting += (_, e) =>
        {
            if (e.Value is double d) { e.Value = Fmt.Qty(d); e.FormattingApplied = true; }
            else if (e.Value is long l && g.Columns[e.ColumnIndex].Name != "id") { e.Value = l.ToString(); e.FormattingApplied = true; }
        };
        g.DataBindingComplete += (_, _) =>
        {
            foreach (DataGridViewColumn c in g.Columns)
                if (c.Name == "id" || c.Name.EndsWith("_id") || c.Name.StartsWith("_") || c.Name == "stored_name" || c.Name == "entity")
                    c.Visible = false;
        };
        return g;
    }

    public static void Bind(this DataGridView g, DataTable t)
    {
        g.DataSource = t;
        foreach (DataGridViewColumn c in g.Columns)
        {
            var sample = t.Rows.Count > 0 ? t.Rows[0][c.Index] : null;
            if (sample is double or long) c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
    }

    public static DataRow? SelRow(this DataGridView g) => (g.CurrentRow?.DataBoundItem as DataRowView)?.Row;

    public static long? SelId(this DataGridView g, string col = "id") => g.SelRow()?.NLng(col);

    /// <summary>Satır rengi kuralı (kritik stok sarı, bitmiş kırmızı vb.).</summary>
    public static void RowColors(this DataGridView g, Func<DataRow, Color?> rule) =>
        g.RowPrePaint += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= g.Rows.Count) return;
            if ((g.Rows[e.RowIndex].DataBoundItem as DataRowView)?.Row is DataRow r && rule(r) is Color c)
                g.Rows[e.RowIndex].DefaultCellStyle.BackColor = c;
        };

    /// <summary>Etiket + kontrol satırları için iki kolonlu form tablosu.</summary>
    public static TableLayoutPanel Fields()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 4) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    public static T Add<T>(this TableLayoutPanel t, string label, T c) where T : Control
    {
        t.RowCount++;
        t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 6, 0) }, 0, t.RowCount - 1);
        if (c is ComboBox or TextBox { Multiline: false } or DateTimePicker or NumericUpDown) c.Dock = DockStyle.Fill;
        c.Margin = new Padding(0, 3, 0, 3);
        t.Controls.Add(c, 1, t.RowCount - 1);
        return c;
    }
}

/// <summary>Altında Kaydet / İptal düğmeleri olan standart pencere.</summary>
public class DialogBase : Form
{
    protected readonly Panel Body = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 12, 16, 8) };
    protected readonly FlowLayoutPanel Buttons = new()
    {
        Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(12, 8, 12, 8),
        BackColor = Color.FromArgb(236, 240, 244),
    };
    protected readonly Button OkButton, CancelBtn;

    public DialogBase(string title, int w = 640, int h = 560, string okText = "Kaydet")
    {
        Text = title; Width = w; Height = h; Font = Ui.Base; StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; ShowInTaskbar = false; BackColor = Color.White;
        OkButton = Ui.Btn(okText, (_, _) => { if (OnSave()) { DialogResult = DialogResult.OK; Close(); } }, Ui.Primary, 120);
        CancelBtn = Ui.Btn("Kapat", (_, _) => Close(), null, 100);
        CancelBtn.DialogResult = DialogResult.Cancel;
        Buttons.Controls.Add(OkButton);
        Buttons.Controls.Add(CancelBtn);
        Controls.Add(Body);
        Controls.Add(Buttons);
        CancelButton = CancelBtn;
    }

    /// <summary>Kaydet'e basınca çağrılır. true dönerse pencere kapanır.</summary>
    protected virtual bool OnSave() => true;
}
