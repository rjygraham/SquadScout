using Microsoft.Extensions.DependencyInjection;

namespace SquadScout.App.Infrastructure;

public static class AppServices
{
    public static IServiceProvider Services { get; private set; } = default!;

    public static void Initialize(IServiceProvider services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public static T GetRequiredService<T>()
        where T : notnull
    {
        if (Services is null)
        {
            throw new InvalidOperationException("The MAUI service provider has not been initialized.");
        }

        return Services.GetRequiredService<T>();
    }
}
