namespace OBK.AndroidApp;

internal sealed class RefreshStatusTimeout(TimeProvider clock)
{
    private long? started;
    internal bool Pending => started.HasValue;

    internal void Start() => started = clock.GetTimestamp();
    internal void Cancel() => started = null;

    internal bool TryExpire()
    {
        if (started is not { } timestamp || clock.GetElapsedTime(timestamp) < TimeSpan.FromMinutes(1))
            return false;
        started = null;
        return true;
    }
}
