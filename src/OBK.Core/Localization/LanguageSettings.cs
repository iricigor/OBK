using System.Globalization;
using System.Text.Json;

namespace OBK.Core.Localization;

public static class LanguageSettings
{
    public static CultureInfo Culture => SharedStrings.Culture ?? CultureInfo.CurrentCulture;
    public static string Code => Culture.TwoLetterISOLanguageName;
    public static string NextCode => Code == "cs" ? "en" : "cs";
    public static string NextName => NextCode == "cs" ? "čeština" : "English";
    public static string SwitchTooltip => Localized.Format(SharedStrings.LanguageSwitchTooltip, NextName);

    public static string Detect(string osLanguage) =>
        osLanguage.Split('-', '_')[0].Equals("en", StringComparison.OrdinalIgnoreCase) ? "en" : "cs";

    public static void Apply(string code)
    {
        if (code is not ("en" or "cs")) throw new ObkException(SharedStrings.ErrorLanguageInvalid);
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
            return setting?.Language is "en" or "cs" ? setting.Language :
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
        if (code is not ("en" or "cs")) throw new ObkException(SharedStrings.ErrorLanguageInvalid);
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

    private sealed record Setting(string Language);
}
