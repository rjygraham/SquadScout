using SquadScout.App.Infrastructure;
using SquadScout.App.ViewModels;

namespace SquadScout.App.Views;

public partial class ActiveSessionPage : ContentPage
{
    public ActiveSessionPage()
    {
        InitializeComponent();
        BindingContext = AppServices.GetRequiredService<ActiveSessionViewModel>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await ((ActiveSessionViewModel)BindingContext).InitializeAsync();
    }
}
