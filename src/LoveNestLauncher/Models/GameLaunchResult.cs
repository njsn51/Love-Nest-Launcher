namespace LoveNestLauncher.Models;

public enum GameLaunchStatus
{
    Launched,
    AlreadyRunning,
    NotFound,
    Failed
}

public sealed record GameLaunchResult(GameLaunchStatus Status, string Message);
