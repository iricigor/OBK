using OBK.App.Localization;
using OBK.Core;
using OBK.Core.Localization;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace OBK.App;

public sealed class ThemeManager(string settingsPath) : ObservableObject
{
    private ResourceDictionary? current;
    private string theme = "Light";
    public string Theme => theme;
    public bool IsDark => theme == "Dark";
    public bool IsLight => !IsDark;
    public string ToggleTooltip => IsDark ? AppStrings.ThemeSwitchToLightTooltip : AppStrings.ThemeSwitchToDarkTooltip;
    public string ToggleIcon => IsDark ? "\uE706" : "\uE708";

    public async Task InitializeAsync()
    {
        var selected = await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(settingsPath)) return "Light";
                var settings = JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(settingsPath));
                return settings?.Theme is "Light" or "Dark" ? settings.Theme :
                    throw new ObkException(AppStrings.ThemeErrorInvalid);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new ObkException(AppStrings.ThemeErrorRead);
            }
        });
        Apply(selected);
    }

    public async Task ToggleAsync() => await SetAsync(IsDark ? "Light" : "Dark");

    public async Task SetAsync(string selected)
    {
        if (selected == theme) return;
        Apply(selected);
        await Task.Run(() =>
        {
            var temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(new ThemeSettings(selected)));
                File.Move(temporary, settingsPath, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new ObkException(AppStrings.ThemeErrorSave);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new ObkException(AppStrings.ThemeErrorCleanup);
                }
            }
        });
    }

    public void Apply(string selected)
    {
        if (selected is not ("Light" or "Dark")) throw new ObkException(AppStrings.ThemeErrorChoose);
        var resources = Application.Current.Resources.MergedDictionaries;
        var next = new ResourceDictionary { Source = new Uri($"/OBK;component/Themes/{selected}.xaml", UriKind.Relative) };
        if (current is not null) resources.Remove(current);
        resources.Add(next);
        current = next;
        theme = selected;
        Notify(nameof(Theme));
        Notify(nameof(IsDark));
        Notify(nameof(IsLight));
        Notify(nameof(ToggleTooltip));
        Notify(nameof(ToggleIcon));
    }

    private sealed record ThemeSettings(string Theme);
}
