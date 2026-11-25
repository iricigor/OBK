using OBK.Core;

namespace OBK.AndroidApp;

/// <summary>Android list grouping: the shared Windows choices plus Child for the all-children scope.</summary>
internal enum ListGrouping { None, Date, Child, Sender, Subject }

internal static class ReaderPreferences
{
    private const string ReadingSizePreference = "reader_reading_size";
    private const string ListDensityPreference = "reader_list_density";
    private const string GroupingPreference = "reader_grouping";
    private const string SortPreference = "reader_sort";
    private const string AllChildrenPreference = "reader_all_children";
    internal static readonly string[] ReadingSizes = ["Small", "Medium", "Large"];
    internal static readonly string[] ListDensities = ["Compact", "Relaxed"];

    internal static string ReadingSize => Preferences.Default.Get(ReadingSizePreference, "Medium");
    internal static string ListDensity => Preferences.Default.Get(ListDensityPreference, "Relaxed");
    internal static ListGrouping Grouping
    {
        get => Enum.TryParse<ListGrouping>(Preferences.Default.Get(GroupingPreference, nameof(ListGrouping.Date)), out var value)
            ? value : ListGrouping.Date;
        set => Preferences.Default.Set(GroupingPreference, value.ToString());
    }
    internal static MessageSort Sort
    {
        get => Enum.TryParse<MessageSort>(Preferences.Default.Get(SortPreference, nameof(MessageSort.Date)), out var value)
            ? value : MessageSort.Date;
        set => Preferences.Default.Set(SortPreference, value.ToString());
    }
    internal static bool AllChildren
    {
        get => Preferences.Default.Get(AllChildrenPreference, true);
        set => Preferences.Default.Set(AllChildrenPreference, value);
    }

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
        resources["ListRowPadding"] = new Thickness(8, compact ? 4 : 8);
        resources["ListLineGap"] = compact ? 0d : 2d;
    }
}
