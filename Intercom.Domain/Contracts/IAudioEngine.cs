using Intercom.Domain.Models;

namespace Intercom.Domain.Contracts;

public interface IAudioEngine
{
    event Action<byte[]>? FrameCaptured;
    event Action<Exception>? Failed;

    void StartPlayback();
    void StartCapture();
    Task StopCaptureAsync();
    Task StopPlaybackAsync();
    void QueuePlayback(Guid speaker, byte[] pcm);
    void SetSettings(AudioSettings settings);
}

