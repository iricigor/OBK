using System.Globalization;

namespace OBK.Core.Localization;

public static class Localized
{
    /// <summary>Fills a localized format resource using the current UI formatting culture.</summary>
    public static string Format(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, format, args);
}
