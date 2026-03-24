using SquadScout.App.Navigation;
using SquadScout.App.Views;

namespace SquadScout.App;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		Routing.RegisterRoute(AppRoutes.ActiveSession, typeof(ActiveSessionPage));
	}
}
