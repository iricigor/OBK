namespace OBK.Core;

public static class RefreshScope
{
    public static IReadOnlyList<Login> Accounts(IReadOnlyList<Login> known, Login? selected,
        bool showingAllChildren, bool alwaysRefreshAllChildren)
    {
        if (showingAllChildren || alwaysRefreshAllChildren) return known;
        if (selected is null) return [];
        var account = known.FirstOrDefault(item => item.Url == selected.Url && item.Username == selected.Username);
        return account is null ? [] : [account];
    }
}
