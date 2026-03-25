using System.ComponentModel;
using System.Linq;
using SquadScout.App.Infrastructure;
using SquadScout.App.ViewModels;

namespace SquadScout.App.Views;

public partial class ActiveSessionPage : ContentPage
{
    private bool _stickToBottom = true;

    public ActiveSessionPage()
    {
        InitializeComponent();
        BindingContext = AppServices.GetRequiredService<ActiveSessionViewModel>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        await ViewModel.InitializeAsync();
        await ScrollToLatestAsync(force: true);
    }

    protected override void OnDisappearing()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnDisappearing();
    }

    private ActiveSessionViewModel ViewModel => (ActiveSessionViewModel)BindingContext;

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ActiveSessionViewModel.TranscriptMessages) and not nameof(ActiveSessionViewModel.HasTranscriptMessages))
        {
            return;
        }

        var latestMessage = ViewModel.TranscriptMessages.LastOrDefault();
        await ScrollToLatestAsync(force: _stickToBottom || latestMessage?.IsOutgoing == true);
    }

    private void OnTranscriptScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        if (ViewModel.TranscriptMessages.Count == 0)
        {
            _stickToBottom = true;
            return;
        }

        _stickToBottom = e.LastVisibleItemIndex >= ViewModel.TranscriptMessages.Count - 2;
    }

    private Task ScrollToLatestAsync(bool force)
    {
        if (!force || ViewModel.TranscriptMessages.Count == 0)
        {
            return Task.CompletedTask;
        }

        var latestMessage = ViewModel.TranscriptMessages[^1];
        return MainThread.InvokeOnMainThreadAsync(() => TranscriptView.ScrollTo(latestMessage, position: ScrollToPosition.End, animate: true));
    }
}
