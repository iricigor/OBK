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
    private const string AlwaysRefreshAllChildrenPreference = "always_refresh_all_children";
    private const string ScheduledChecksPreference = "scheduled_checks";
    private const string CheckNotificationsPreference = "scheduled_check_notifications";
    private const string EmptyCheckNotificationsPreference = "scheduled_check_empty_notifications";
    internal static readonly string[] ReadingSizes = ["Small", "Medium", "Large"];
    internal static readonly string[] ListDensities = ["Compact", "Relaxed"];

    internal static string ReadingSize => Preferences.Default.Get(ReadingSizePreference, "Medium");
    internal static string ListDensity => Preferences.Default.Get(ListDensityPreference, "Relaxed");
    internal static bool CompactDensity => ListDensity == "Compact";
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
    internal static bool AlwaysRefreshAllChildren
    {
        get => Preferences.Default.Get(AlwaysRefreshAllChildrenPreference, false);
        set => Preferences.Default.Set(AlwaysRefreshAllChildrenPreference, value);
    }
    /// <summary>Hourly background checks; turning them off also turns off both notification settings below.</summary>
    internal static bool ScheduledChecks
    {
        get => Preferences.Default.Get(ScheduledChecksPreference, false);
        set
        {
            Preferences.Default.Set(ScheduledChecksPreference, value);
            if (!value) CheckNotifications = false;
        }
    }
    /// <summary>Only on while scheduled checks are on; turning it off also turns off empty-check notifications.</summary>
    internal static bool CheckNotifications
    {
        get => Nested(CheckNotificationsPreference);
        set
        {
            Preferences.Default.Set(CheckNotificationsPreference, value);
            if (!value) EmptyCheckNotifications = false;
        }
    }
    internal static bool EmptyCheckNotifications
    {
        get => Nested(EmptyCheckNotificationsPreference);
        set => Preferences.Default.Set(EmptyCheckNotificationsPreference, value);
    }

    /// <summary>
    /// Reads a nested check setting after saving each one under an off parent as off; earlier builds could leave them on.
    /// </summary>
    private static bool Nested(string key)
    {
        var parent = ScheduledChecks;
        foreach (var nested in new[] { CheckNotificationsPreference, EmptyCheckNotificationsPreference })
        {
            var value = Preferences.Default.Get(nested, false);
            if (value && !parent) Preferences.Default.Set(nested, false);
            parent = value && parent;
        }
        return Preferences.Default.Get(key, false);
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
