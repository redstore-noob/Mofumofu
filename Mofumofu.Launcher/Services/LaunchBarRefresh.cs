using System;

namespace Mofumofu.Launcher.Services;

public static class LaunchBarRefresh
{
    public static event Action? Requested;

    public static void Request() => Requested?.Invoke();
}
