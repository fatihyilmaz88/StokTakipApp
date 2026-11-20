using System.Globalization;

namespace StokTakip.Core;

/// <summary>Türkçe sayı/para biçimlendirme ve sayı okuma.</summary>
public static class Fmt
{
    public static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Qty(double v) => v.ToString("#,##0.###", Tr);
    public static string Qty(object? v) => v is null or DBNull ? "" : Qty(Convert.ToDouble(v));

    public static string Money(double v, string? cur)
    {
        var sym = cur switch { "EUR" => "€", "USD" => "$", null or "" or "TRY" => "₺", _ => cur };
        return v.ToString("#,##0.00", Tr) + " " + sym;
    }

    /// <summary>"1.234,5" / "1234.5" / "12,5" biçimlerini okur. Boşsa def, hatalıysa null.</summary>
    public static double? ParseNum(string? s, double? def = null)
    {
        if (string.IsNullOrWhiteSpace(s)) return def;
        s = s.Trim().Replace(" ", "");
        if (s.Contains(',')) s = s.Replace(".", "").Replace(",", ".");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Excel için binlik ayırıcısız.</summary>
    public static string Xl(object? v) => v is null or DBNull ? "" : Convert.ToDouble(v).ToString("0.###", Tr);
}
