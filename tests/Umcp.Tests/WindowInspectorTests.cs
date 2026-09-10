using Umcp.Daemon.Agent;
using Xunit;

namespace Umcp.Tests;

public sealed class WindowInspectorTests
{
    [Fact]
    public void An_owned_enabled_unity_tool_window_is_not_a_dialog()
    {
        Assert.False(WindowInspector.LooksBlocking("UnityContainerWndClass", hasOwner: true,
            ownerEnabled: true));
    }

    [Fact]
    public void An_owned_window_with_a_disabled_owner_is_blocking()
    {
        Assert.True(WindowInspector.LooksBlocking("UnityContainerWndClass", hasOwner: true,
            ownerEnabled: false));
    }

    [Fact]
    public void A_native_dialog_is_blocking_even_without_an_owner()
    {
        Assert.True(WindowInspector.LooksBlocking("#32770", hasOwner: false, ownerEnabled: true));
    }
}
