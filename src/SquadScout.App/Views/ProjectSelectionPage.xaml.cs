using SquadScout.App.Infrastructure;
using SquadScout.App.ViewModels;

namespace SquadScout.App.Views;

public partial class ProjectSelectionPage : ContentPage
{
    public ProjectSelectionPage()
    {
        InitializeComponent();
        BindingContext = AppServices.GetRequiredService<ProjectSelectionViewModel>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await ((ProjectSelectionViewModel)BindingContext).InitializeAsync();
    }
}
