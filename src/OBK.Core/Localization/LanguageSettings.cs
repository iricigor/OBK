using System.Globalization;
using System.Text.Json;

namespace OBK.Core.Localization;

public static class LanguageSettings
{
    private static readonly IReadOnlyList<string> supportedCodes =
        Array.AsReadOnly(["en", "cs", "uk", "de"]);

    public static CultureInfo Culture => SharedStrings.Culture ?? CultureInfo.CurrentCulture;
    public static string Code => Culture.TwoLetterISOLanguageName;
    public static IReadOnlyList<string> SupportedCodes => supportedCodes;
    public static string NextCode
    {
        get
        {
            var index = IndexOf(Code);
            return supportedCodes[index < 0 ? 1 : (index + 1) % supportedCodes.Count];
        }
    }
    public static string NextName => NextCode switch
    {
        "en" => "English",
        "cs" => "čeština",
        "uk" => "українська",
        "de" => "Deutsch",
        _ => throw new InvalidOperationException("Unsupported interface language.")
    };
    public static string SwitchTooltip => Localized.Format(SharedStrings.LanguageSwitchTooltip, NextName);

    public static string Detect(string osLanguage)
    {
        var candidate = osLanguage.Split('-', '_')[0];
        return supportedCodes.FirstOrDefault(code =>
            code.Equals(candidate, StringComparison.OrdinalIgnoreCase)) ?? "cs";
    }

    public static void Apply(string code)
    {
        if (IndexOf(code) < 0) throw new ObkException(SharedStrings.ErrorLanguageInvalid);
        var culture = CultureInfo.GetCultureInfo(code);
        SharedStrings.Culture = culture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
    }

    public static Task<string> LoadAsync(string path, string osLanguage) => Task.Run(() =>
    {
        try
        {
            var setting = JsonSerializer.Deserialize<Setting>(File.ReadAllText(path));
            return setting is not null && IndexOf(setting.Language) >= 0 ? setting.Language :
                throw new ObkException(SharedStrings.ErrorLanguageInvalid);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Detect(osLanguage);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ObkException(SharedStrings.ErrorLanguageRead);
        }
    });

    public static Task SaveAsync(string path, string code)
    {
        if (IndexOf(code) < 0) throw new ObkException(SharedStrings.ErrorLanguageInvalid);
        return Task.Run(() =>
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(new Setting(code)));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new ObkException(SharedStrings.ErrorLanguageSave);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new ObkException(SharedStrings.ErrorLanguageCleanup);
                }
            }
        });
    }

    private static int IndexOf(string code)
    {
        for (var index = 0; index < supportedCodes.Count; index++)
            if (supportedCodes[index].Equals(code, StringComparison.Ordinal))
                return index;
        return -1;
    }

    private sealed record Setting(string Language);
}
