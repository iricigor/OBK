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
    private string readingSize = "Medium";
    private string listDensity = "Relaxed";
    private bool? allChildren;
    private bool alwaysRefreshAllChildren;
    private int refreshIntervalMinutes;
    private readonly SemaphoreSlim saveGate = new(1, 1);
    public string Theme => theme;
    public string ReadingSize => readingSize;
    public bool IsSmallText => readingSize == "Small";
    public bool IsMediumText => readingSize == "Medium";
    public bool IsLargeText => readingSize == "Large";
    public string ListDensity => listDensity;
    public bool IsCompactList => listDensity == "Compact";
    public bool IsRelaxedList => listDensity == "Relaxed";
    public bool IsDark => theme == "Dark";
    public bool IsLight => !IsDark;
    public string ToggleTooltip => IsDark ? AppStrings.ThemeSwitchToLightTooltip : AppStrings.ThemeSwitchToDarkTooltip;
    public string ToggleIcon => IsDark ? "\uE706" : "\uE708";
    /// <summary>The remembered reader scope: true for All children, false for one child, null until chosen.</summary>
    public bool? AllChildren => allChildren;
    public bool AlwaysRefreshAllChildren => alwaysRefreshAllChildren;
    public int RefreshIntervalMinutes => refreshIntervalMinutes;

    public async Task InitializeAsync()
    {
        var loaded = await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(settingsPath)) return new ThemeSettings("Light");
                var settings = JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(settingsPath));
                return settings?.Theme is "Light" or "Dark" ? settings :
                    throw new ObkException(AppStrings.ThemeErrorInvalid);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new ObkException(AppStrings.ThemeErrorRead);
            }
        });
        RefreshIntervals.Validate(loaded.RefreshIntervalMinutes);
        ApplyScale(loaded.ReadingSize is "Small" or "Large" ? loaded.ReadingSize : "Medium");
        ApplyDensity(loaded.ListDensity == "Compact" ? "Compact" : "Relaxed");
        Apply(loaded.Theme);
        allChildren = loaded.AllChildren;
        alwaysRefreshAllChildren = loaded.AlwaysRefreshAllChildren;
        Notify(nameof(AlwaysRefreshAllChildren));
        refreshIntervalMinutes = loaded.RefreshIntervalMinutes;
        Notify(nameof(RefreshIntervalMinutes));
    }

    public async Task ToggleAsync() => await SetAsync(IsDark ? "Light" : "Dark");

    public async Task SetAsync(string selected)
    {
        if (selected == theme) return;
        Apply(selected);
        await SaveAsync();
    }

    public async Task SetReadingSizeAsync(string selected)
    {
        if (selected is not ("Small" or "Medium" or "Large")) throw new ObkException(AppStrings.ThemeErrorChoose);
        if (selected == readingSize) return;
        ApplyScale(selected);
        await SaveAsync();
    }

    public async Task SetListDensityAsync(string selected)
    {
        if (selected is not ("Compact" or "Relaxed")) throw new ObkException(AppStrings.ThemeErrorChoose);
        if (selected == listDensity) return;
        ApplyDensity(selected);
        await SaveAsync();
    }

    /// <summary>Remembers the reader scope; the new value applies immediately, even if saving fails.</summary>
    public async Task SetAllChildrenAsync(bool selected)
    {
        if (allChildren == selected) return;
        allChildren = selected;
        await SaveAsync();
    }

    public async Task SetAlwaysRefreshAllChildrenAsync(bool selected)
    {
        if (alwaysRefreshAllChildren == selected) return;
        alwaysRefreshAllChildren = selected;
        Notify(nameof(AlwaysRefreshAllChildren));
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        await saveGate.WaitAsync();
        try
        {
            var settings = new ThemeSettings(theme, readingSize, listDensity, allChildren, alwaysRefreshAllChildren, refreshIntervalMinutes);
            await Task.Run(() =>
            {
                var temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                    File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
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
        finally { saveGate.Release(); }
    }

    public async Task SetRefreshIntervalAsync(int minutes)
    {
        RefreshIntervals.Validate(minutes);
        if (refreshIntervalMinutes == minutes) return;
        refreshIntervalMinutes = minutes;
        Notify(nameof(RefreshIntervalMinutes));
        await SaveAsync();
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

    private void ApplyScale(string selected)
    {
        var factor = selected switch { "Small" => 0.9, "Large" => 1.15, _ => 1.0 };
        var resources = Application.Current.Resources;
        double Size(double medium) => Math.Round(medium * factor * 2) / 2;
        resources["ListSubjectSize"] = Size(13);
        resources["ListSenderSize"] = Size(12);
        resources["ListDateSize"] = Size(11.5);
        resources["ReaderSubjectSize"] = Size(18);
        resources["ReaderSenderSize"] = Size(14);
        resources["ReaderMetaSize"] = Size(12);
        resources["ReaderBodySize"] = Size(14);
        resources["ReaderLineHeight"] = Math.Round(21 * factor);
        readingSize = selected;
        Notify(nameof(ReadingSize));
        Notify(nameof(IsSmallText));
        Notify(nameof(IsMediumText));
        Notify(nameof(IsLargeText));
    }

    private void ApplyDensity(string selected)
    {
        var compact = selected == "Compact";
        var resources = Application.Current.Resources;
        resources["ListRowPadding"] = compact ? new Thickness(12, 4, 12, 4) : new Thickness(12, 8, 12, 8);
        resources["ListLineGap"] = compact ? new Thickness(0) : new Thickness(0, 2, 0, 0);
        resources["ListBadgeMargin"] = compact ? new Thickness(8, 0, 0, 0) : new Thickness(8, 2, 0, 0);
        listDensity = selected;
        Notify(nameof(ListDensity));
        Notify(nameof(IsCompactList));
        Notify(nameof(IsRelaxedList));
    }

    private sealed record ThemeSettings(string Theme, string? ReadingSize = null, string? ListDensity = null,
        bool? AllChildren = null, bool AlwaysRefreshAllChildren = false, int RefreshIntervalMinutes = 0);
}
