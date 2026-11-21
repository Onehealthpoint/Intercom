using Android.App;
using Android.Content.PM;
using Intercom.Application.Services;

namespace Intercom.UI;

[Activity(Theme = "@style/Maui.MainTheme.NoActionBar", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
        ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private static IntercomSessionManager? Session =>
        IPlatformApplication.Current?.Services.GetService<IntercomSessionManager>();

    protected override void OnResume()
    {
        base.OnResume();
        Session?.OnForegroundChanged(true);
    }

    protected override void OnPause()
    {
        if (Session is { } session)
            _ = session.SetPushToTalkAsync(false);
        base.OnPause();
    }

    protected override async void OnStop()
    {
        base.OnStop();
        if (!IsChangingConfigurations && Session is { } session)
        {
            session.OnForegroundChanged(false);
            await session.OnStoppedAsync();
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
            return;

        Microsoft.Maui.ApplicationModel.Platform.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }
}

