namespace Intercom.Domain.Models;

public sealed record AudioSettings(float InputGain = 1, bool ReduceNoise = true, int NoiseReduction = 12);

public static class VoiceFrame
{
    public const int SampleRate = 16000;
    public const int Milliseconds = 20;
    public const int Size = SampleRate * Milliseconds / 1000 * sizeof(short);
}

