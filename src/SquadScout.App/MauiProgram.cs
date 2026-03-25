using SquadScout.App.Configuration;
using SquadScout.App.Infrastructure;
using SquadScout.App.Navigation;
using SquadScout.App.Services;
using SquadScout.App.ViewModels;
using Microsoft.Extensions.Hosting;

namespace SquadScout.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder.AddServiceDefaults();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		var environment = AppConfiguration.LoadEnvironment();
		var bootstrapOptions = AppConfiguration.LoadOptions(environment);
		var brokerApiOptions = bootstrapOptions.BrokerApi;
		var authOptions = bootstrapOptions.Auth;
		var messagingOptions = bootstrapOptions.Messaging;
		var localDevelopmentOptions = bootstrapOptions.LocalDevelopment;

		builder.Services.AddSingleton(environment);
		builder.Services.AddSingleton(brokerApiOptions);
		builder.Services.AddSingleton(authOptions);
		builder.Services.AddSingleton(messagingOptions);
		builder.Services.AddSingleton(localDevelopmentOptions);
		builder.Services.AddHttpClient("BrokerApi", client =>
		{
			client.BaseAddress = AppConfiguration.CreateBrokerBaseUri(brokerApiOptions);
			client.Timeout = TimeSpan.FromSeconds(Math.Max(1, brokerApiOptions.RequestTimeoutSeconds));
		});
		builder.Services.AddSingleton<Func<HttpClient>>(
			static services => () => services.GetRequiredService<IHttpClientFactory>().CreateClient("BrokerApi"));
		builder.Services.AddSingleton<IAppNavigator, ShellNavigator>();
		builder.Services.AddSingleton<IAuthenticationService, ConfiguredAuthenticationService>();
		builder.Services.AddSingleton<IMessageConnectionService, MessagingConnectionService>();
		builder.Services.AddSingleton<IProjectCatalogService, BrokerProjectCatalogService>();
		builder.Services.AddSingleton<ISessionLifecycleService, BrokerSessionLifecycleService>();
		builder.Services.AddSingleton<IActiveSessionState, ActiveSessionState>();
		builder.Services.AddSingleton<ProjectSelectionViewModel>();
		builder.Services.AddSingleton<ActiveSessionViewModel>();

		var app = builder.Build();
		AppServices.Initialize(app.Services);
		return app;
	}
}
