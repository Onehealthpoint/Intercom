using Android.App;
using Android.Content;
using Android.OS;
using ServiceType = Android.Content.PM.ForegroundService;

namespace Intercom.Infrastructure.Android.Services;

[Service(Exported = false, ForegroundServiceType = ServiceType.TypeMicrophone | ServiceType.TypeMediaPlayback)]
public sealed class IntercomForegroundService : Service
{
    private const int NotificationId = 1001;
    private const string ChannelId = "intercom-session";
    private const string EndAction = "intercom.END_SESSION";
    internal const string SessionExtra = "intercom.SESSION_ID";

    private int _activeId;
    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var id = intent?.GetIntExtra(SessionExtra, 0) ?? 0;
        var controller = AndroidBackgroundSession.Instance;
        if (!controller.IsCurrent(id))
        {
            if (_activeId == 0 || !controller.IsCurrent(_activeId))
                StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        if (intent?.Action == EndAction)
        {
            controller.ServiceEnded(id);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        try
        {
            _activeId = id;
            using var notification = CreateNotification(id);
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                StartForeground(NotificationId, notification, ServiceType.TypeMicrophone | ServiceType.TypeMediaPlayback);
            else
                StartForeground(NotificationId, notification);
            controller.Started(id);

            if (_wakeLock is null && GetSystemService(PowerService) is PowerManager power)
            {
                _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "RiderIntercom:HandsFree");
                _wakeLock?.Acquire();
            }
        }
        catch (Exception error)
        {
            controller.StartFailed(id, error);
            StopSelf();
        }
        return StartCommandResult.NotSticky;
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        AndroidBackgroundSession.Instance.ServiceEnded(_activeId);
        StopSelf();
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        AndroidBackgroundSession.Instance.ServiceEnded(_activeId);
        if (_wakeLock?.IsHeld == true)
            _wakeLock.Release();
        _wakeLock?.Dispose();
        _wakeLock = null;
        base.OnDestroy();
    }

    private Notification CreateNotification(int id)
    {
        Notification.Builder builder;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            using var channel = new NotificationChannel(ChannelId, "Intercom session", NotificationImportance.Low);
            (GetSystemService(NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
            builder = new Notification.Builder(this, ChannelId);
        }
        else
        {
#pragma warning disable CS0618
            builder = new Notification.Builder(this);
#pragma warning restore CS0618
        }

        using (builder)
        using (var endIntent = AndroidBackgroundSession.CreateIntent(id))
        using (var openIntent = PackageManager?.GetLaunchIntentForPackage(PackageName!))
        {
            endIntent.SetAction(EndAction);
            var immutable = OperatingSystem.IsAndroidVersionAtLeast(23) ? PendingIntentFlags.Immutable : 0;
            using var end = PendingIntent.GetService(this, id, endIntent, immutable);
            using var open = openIntent is null ? null :
                PendingIntent.GetActivity(this, 0, openIntent, immutable | PendingIntentFlags.UpdateCurrent);
            builder.SetContentTitle("Intercom · Hands-free");
            builder.SetContentText("Voice session active");
            builder.SetSmallIcon(global::Android.Resource.Drawable.StatSysHeadset);
            builder.SetOngoing(true);
            if (open is not null)
                builder.SetContentIntent(open);
            using var label = new Java.Lang.String("End session");
            const int endIcon = global::Android.Resource.Drawable.IcMenuCloseClearCancel;
            if (OperatingSystem.IsAndroidVersionAtLeast(23))
            {
                using var icon = global::Android.Graphics.Drawables.Icon.CreateWithResource(this, endIcon);
                using var actionBuilder = new Notification.Action.Builder(icon, label, end);
                using var action = actionBuilder.Build();
                builder.AddAction(action);
            }
            else
            {
#pragma warning disable CS0618
                using var actionBuilder = new Notification.Action.Builder(endIcon, label, end);
#pragma warning restore CS0618
                using var action = actionBuilder.Build();
                builder.AddAction(action);
            }
            return builder.Build()!;
        }
    }
}

