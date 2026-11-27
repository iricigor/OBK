using OBK.Core.Localization;

namespace OBK.Core;

public static class RefreshIntervals
{
    public static IReadOnlyList<int> Minutes { get; } = Array.AsReadOnly<int>([0, 15, 30, 60, 240, 1440]);

    public static string Label(int minutes) => minutes switch
    {
        0 => SharedStrings.RefreshOff,
        15 => SharedStrings.Refresh15Minutes,
        30 => SharedStrings.Refresh30Minutes,
        60 => SharedStrings.Refresh1Hour,
        240 => SharedStrings.Refresh4Hours,
        1440 => SharedStrings.Refresh24Hours,
        _ => throw new ObkException(SharedStrings.ErrorRefreshInterval)
    };

    public static void Validate(int minutes)
    {
        if (!Minutes.Contains(minutes)) throw new ObkException(SharedStrings.ErrorRefreshInterval);
    }
}
