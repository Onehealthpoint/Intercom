namespace Intercom.Domain.Contracts;

public enum MappableButton
{
    VolumeDown,
    VolumeUp,
    HeadsetHook,
    MediaPlayPause
}

public interface IHardwareKeyService
{
    event EventHandler<bool>? OnPttStateChanged;
    void RegisterPttButton(MappableButton button);
    bool HandleKeyEvent(int keyCode, bool isKeyDown);
}