using Umcp.Gui.Services;
using Xunit;

namespace Umcp.Tests;

/// <summary>
/// "Start when I sign in" writes to a real place — HKCU's Run key on Windows, a LaunchAgent plist
/// on macOS — so these tests exercise the real one and put it back. Nothing else can prove the
/// checkbox does anything, and a startup entry that silently fails to persist is exactly the kind
/// of setting nobody notices until the day they reboot.
/// </summary>
public class LoginItemTests
{
    static bool Runnable => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    [Fact]
    public void Enabling_then_disabling_leaves_no_trace()
    {
        if (!Runnable) return;                       // Linux CI has no login-item concept here

        var before = LoginItem.IsEnabled();
        try
        {
            Assert.Null(LoginItem.Set(true));
            Assert.True(LoginItem.IsEnabled());

            Assert.Null(LoginItem.Set(false));
            Assert.False(LoginItem.IsEnabled());
        }
        finally
        {
            // Whatever the developer running these tests had before, they get back.
            LoginItem.Set(before);
        }
    }

    [Fact]
    public void Setting_it_twice_is_not_an_error()
    {
        if (!Runnable) return;

        var before = LoginItem.IsEnabled();
        try
        {
            LoginItem.Set(true);
            Assert.Null(LoginItem.Set(true));
            Assert.True(LoginItem.IsEnabled());

            LoginItem.Set(false);
            // Removing an entry that is already gone is a no-op, not a failure: the checkbox can
            // be unticked after somebody removed it in Task Manager.
            Assert.Null(LoginItem.Set(false));
        }
        finally
        {
            LoginItem.Set(before);
        }
    }

    [Fact]
    public void It_is_supported_on_the_platforms_the_app_ships_to()
    {
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), LoginItem.Supported);
    }
}
