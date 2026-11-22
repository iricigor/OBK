namespace OBK.Core;

public static class AvatarPresentation
{
    public const int PaletteCount = 6;

    public static string Initials(string label) =>
        string.Concat(label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2)
            .Select(word => System.Globalization.StringInfo.GetNextTextElement(word))).ToUpperInvariant();

    public static int PaletteIndex(string? value)
    {
        // FNV-1a keeps assignments stable across launches, unlike string.GetHashCode.
        var hash = 2166136261u;
        foreach (var c in (value ?? "").Trim().ToLowerInvariant())
            hash = (hash ^ c) * 16777619u;
        return (int)(hash % PaletteCount);
    }

    public static bool UseLargeInitial(Login? account, IReadOnlyList<Login> known)
    {
        if (string.IsNullOrWhiteSpace(account?.ChildName)) return false;
        static bool OneWord(string name) => name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length == 1;
        return OneWord(account.ChildName) && known.Where(login => !string.IsNullOrWhiteSpace(login.ChildName))
            .All(login => OneWord(login.ChildName!));
    }
}
