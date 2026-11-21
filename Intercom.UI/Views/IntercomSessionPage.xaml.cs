using Android.Views;
using Intercom.UI.ViewModels;

namespace Intercom.UI.Views;

public partial class IntercomSessionPage : ContentView
{
    private IntercomViewModel? ViewModel => BindingContext as IntercomViewModel;

    public IntercomSessionPage() => InitializeComponent();

    private void OnTalkHandlerChanged(object? sender, EventArgs e)
    {
        if ((sender as Button)?.Handler?.PlatformView is global::Android.Views.View platformView)
            platformView.Touch += OnTalkNativeTouch;
    }

    private async void OnTalkHandlerChanging(object? sender, HandlerChangingEventArgs e)
    {
        if (e.OldHandler?.PlatformView is global::Android.Views.View platformView)
        {
            platformView.Touch -= OnTalkNativeTouch;
            if (ViewModel is { } viewModel)
                await viewModel.HoldToTalkAsync(false);
        }
    }

    private async void OnTalkNativeTouch(object? sender, global::Android.Views.View.TouchEventArgs e)
    {
        e.Handled = false;
        if (e.Event is not { } touch || sender is not global::Android.Views.View view || ViewModel is not { } viewModel)
            return;

        if (touch.ActionMasked == MotionEventActions.Down && view.Enabled)
            await viewModel.HoldToTalkAsync(true);
        else if (touch.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel or MotionEventActions.PointerUp ||
            (touch.ActionMasked == MotionEventActions.Move &&
                (touch.GetX() < 0 || touch.GetX() >= view.Width || touch.GetY() < 0 || touch.GetY() >= view.Height)))
            await viewModel.HoldToTalkAsync(false);
    }

    private async void OnHandsFreeToggled(object? sender, ToggledEventArgs e)
    {
        if (sender is Switch toggle && ViewModel is { } viewModel && e.Value != viewModel.HandsFree)
        {
            await viewModel.SetHandsFreeAsync(e.Value);
            toggle.IsToggled = viewModel.HandsFree;
        }
    }
}

