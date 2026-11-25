using System.Globalization;

namespace OBK.Core.Localization;

public static class Localized
{
    /// <summary>Fills a localized format resource using the current UI formatting culture.</summary>
    public static string Format(string format, params object?[] args) =>
        string.Format(LanguageSettings.Culture, format, args);

    /// <summary>
    /// Fills the plural form of <paramref name="count"/>. Czech and Ukrainian distinguish one, few and many;
    /// English and German use one and many.
    /// </summary>
    public static string Plural(int count, string one, string few, string many)
    {
        var form = LanguageSettings.Code switch
        {
            "cs" => count == 1 ? one : count is >= 2 and <= 4 ? few : many,
            "uk" => count % 10 == 1 && count % 100 != 11 ? one :
                (count % 10 is >= 2 and <= 4) && (count % 100 is < 12 or > 14) ? few : many,
            _ => count == 1 ? one : many
        };
        return Format(form, count);
    }
}
