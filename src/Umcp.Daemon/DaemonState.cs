namespace Umcp.Daemon;

/// <summary>Live daemon state the tray UI can change and the dispatcher reads.</summary>
public sealed class DaemonState
{
    public volatile bool Paused;
}
