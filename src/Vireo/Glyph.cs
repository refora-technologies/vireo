namespace Vireo;

/// <summary>Icon characters from Segoe Fluent Icons, written as codes so they survive any editor.</summary>
internal static class Glyph
{
    private static string G(int code) => ((char)code).ToString();

    public static readonly string Reload = G(0xE72C);
    public static readonly string Stop = G(0xE711);
    public static readonly string Add = G(0xE710);
    public static readonly string Maximize = G(0xE922);
    public static readonly string Restore = G(0xE923);
    public static readonly string Search = G(0xE721);
    public static readonly string Lock = G(0xE72E);
    public static readonly string Warning = G(0xE7BA);
    public static readonly string Speaker = G(0xE767);
    public static readonly string Moon = G(0xE708);
    public static readonly string Globe = G(0xE774);
    public static readonly string Star = G(0xE734);
    public static readonly string StarFilled = G(0xE735);
}
