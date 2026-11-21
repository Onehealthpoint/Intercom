using Intercom.Application.Services;
using Intercom.Domain.Contracts;
using Intercom.Infrastructure.Android.Audio;
using Intercom.Infrastructure.Android.Services;
using Intercom.Infrastructure.Audio;
using Intercom.Infrastructure.Network;
using Intercom.UI.ViewModels;

namespace Intercom.UI;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder()
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<INoiseFilterService, WebRtcNoiseSuppressionService>();
        builder.Services.AddSingleton<INetworkDiscoveryService, UdpDiscoveryService>();
        builder.Services.AddSingleton<IIntercomTransport, UdpSocketAudioTransport>();
        builder.Services.AddSingleton<IAudioEngine, AndroidAudioEngine>();
        builder.Services.AddSingleton<IBackgroundSession>(AndroidBackgroundSession.Instance);
        builder.Services.AddSingleton<IntercomSessionManager>();
        builder.Services.AddSingleton<IntercomViewModel>();
        builder.Services.AddTransient<MainPage>();

        return builder.Build();
    }
}

