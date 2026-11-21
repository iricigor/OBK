using OBK.Core;
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
    public string ToggleTooltip => $"Switch to {(IsDark ? "Light" : "Dark")} theme";
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
                    throw new ObkException("The theme setting is invalid. Choose Light or Dark to replace it.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new ObkException("Cannot read the theme setting. Choose a theme to save it again.");
            }
        });
        Apply(selected);
    }

    public async Task ToggleAsync()
    {
        Apply(IsDark ? "Light" : "Dark");
        var selected = theme;
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
                throw new ObkException("The theme changed, but could not be saved. Check disk access.");
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new ObkException("Could not remove a temporary theme setting file. Check disk access.");
                }
            }
        });
    }

    public void Apply(string selected)
    {
        if (selected is not ("Light" or "Dark")) throw new ObkException("Choose Light or Dark.");
        var resources = Application.Current.Resources.MergedDictionaries;
        var next = new ResourceDictionary { Source = new Uri($"/OBK;component/Themes/{selected}.xaml", UriKind.Relative) };
        if (current is not null) resources.Remove(current);
        resources.Add(next);
        current = next;
        theme = selected;
        Notify(nameof(Theme));
        Notify(nameof(IsDark));
        Notify(nameof(ToggleTooltip));
        Notify(nameof(ToggleIcon));
    }

    private sealed record ThemeSettings(string Theme);
}
