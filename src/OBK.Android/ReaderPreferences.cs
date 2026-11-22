namespace OBK.AndroidApp;

internal static class ReaderPreferences
{
    private const string ReadingSizePreference = "reader_reading_size";
    private const string ListDensityPreference = "reader_list_density";
    internal static readonly string[] ReadingSizes = ["Small", "Medium", "Large"];
    internal static readonly string[] ListDensities = ["Compact", "Relaxed"];

    internal static string ReadingSize => Preferences.Default.Get(ReadingSizePreference, "Medium");
    internal static string ListDensity => Preferences.Default.Get(ListDensityPreference, "Relaxed");

    internal static void Initialize()
    {
        ApplyReadingSize(ReadingSize);
        ApplyListDensity(ListDensity);
    }

    internal static void SetReadingSize(string selected)
    {
        ApplyReadingSize(selected);
        Preferences.Default.Set(ReadingSizePreference, selected);
    }

    internal static void SetListDensity(string selected)
    {
        ApplyListDensity(selected);
        Preferences.Default.Set(ListDensityPreference, selected);
    }

    private static void ApplyReadingSize(string selected)
    {
        var factor = selected switch
        {
            "Small" => 0.9,
            "Medium" => 1.0,
            "Large" => 1.15,
            _ => throw new InvalidOperationException("Unknown reading size preference.")
        };
        var resources = Application.Current!.Resources;
        double Size(double medium) => Math.Round(medium * factor * 2) / 2;
        resources["ListSubjectSize"] = Size(15);
        resources["ListSenderSize"] = Size(13);
        resources["ListDateSize"] = Size(12);
        resources["ReaderSubjectSize"] = Size(20);
        resources["ReaderSenderSize"] = Size(14);
        resources["ReaderMetaSize"] = Size(13);
        resources["ReaderBodySize"] = Size(16);
    }

    private static void ApplyListDensity(string selected)
    {
        var compact = selected switch
        {
            "Compact" => true,
            "Relaxed" => false,
            _ => throw new InvalidOperationException("Unknown list density preference.")
        };
        var resources = Application.Current!.Resources;
        resources["ListRowPadding"] = new Thickness(16, compact ? 4 : 8);
        resources["ListLineGap"] = compact ? 0d : 2d;
    }
}
