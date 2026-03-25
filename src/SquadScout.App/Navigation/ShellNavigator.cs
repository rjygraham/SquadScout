namespace SquadScout.App.Navigation;

public static class AppRoutes
{
    public const string Projects = "projects";
    public const string ActiveSession = "active-session";
}

public interface IAppNavigator
{
    Task GoToProjectsAsync();

    Task GoToActiveSessionAsync();
}

public sealed class ShellNavigator : IAppNavigator
{
    public Task GoToProjectsAsync() =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var shell = Shell.Current ?? throw new InvalidOperationException("Shell navigation is unavailable.");
            var targetRoute = $"//{AppRoutes.Projects}";
            if (string.Equals(shell.CurrentState.Location.OriginalString, targetRoute, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await shell.GoToAsync(targetRoute);
        });

    public Task GoToActiveSessionAsync() =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var shell = Shell.Current ?? throw new InvalidOperationException("Shell navigation is unavailable.");
            if (shell.CurrentState.Location.OriginalString.Contains(AppRoutes.ActiveSession, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await shell.GoToAsync(AppRoutes.ActiveSession);
        });
}
