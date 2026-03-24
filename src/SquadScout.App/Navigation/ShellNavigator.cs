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
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync($"//{AppRoutes.Projects}"));

    public Task GoToActiveSessionAsync() =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(AppRoutes.ActiveSession));
}
