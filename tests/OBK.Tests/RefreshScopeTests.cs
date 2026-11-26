using OBK.Core;
using Xunit;

namespace OBK.Tests;

public sealed class RefreshScopeTests
{
    private static readonly Login Anna = new("https://school.example", "anna", "password", "Anna", "School");
    private static readonly Login Tomas = new("https://school.example", "tomas", "password", "Tomáš", "School");
    private static readonly IReadOnlyList<Login> Known = [Anna, Tomas];

    [Fact]
    public void OneChildRefreshesOnlyTheSelectedChild()
    {
        Assert.Equal([Anna], RefreshScope.Accounts(Known, Anna with { Password = null }, false, false));
    }

    [Fact]
    public void AllChildrenRefreshesEveryChild()
    {
        Assert.Same(Known, RefreshScope.Accounts(Known, Anna, true, false));
    }

    [Fact]
    public void AlwaysRefreshAllOverridesOneChildScope()
    {
        Assert.Same(Known, RefreshScope.Accounts(Known, Anna, false, true));
    }

    [Fact]
    public void OneChildScopeWithoutAKnownSelectionHasNothingToRefresh()
    {
        Assert.Empty(RefreshScope.Accounts(Known, new Login("https://other.example", "anna", null), false, false));
        Assert.Empty(RefreshScope.Accounts(Known, null, false, false));
    }
}
