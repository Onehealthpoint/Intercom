using Android.Views;
using Intercom.Domain.Contracts;

namespace Intercom.Infrastructure.Android.Hardware;

public class AndroidHardwareKeyService : IHardwareKeyService
{
    private MappableButton _configuredButton = MappableButton.VolumeDown;
    public event EventHandler<bool>? OnPttStateChanged;

    public void RegisterPttButton(MappableButton button) => _configuredButton = button;

    public bool HandleKeyEvent(int keyCode, bool isKeyDown)
    {
        var targetKey = _configuredButton switch
        {
            MappableButton.VolumeDown => Keycode.VolumeDown,
            MappableButton.VolumeUp => Keycode.VolumeUp,
            MappableButton.HeadsetHook => Keycode.Headsethook,
            MappableButton.MediaPlayPause => Keycode.MediaPlayPause,
            _ => Keycode.VolumeDown
        };

        if (keyCode == (int)targetKey)
        {
            OnPttStateChanged?.Invoke(this, isKeyDown);
            return true;
        }

        return false;
    }
}