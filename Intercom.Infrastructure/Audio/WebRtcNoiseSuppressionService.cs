using Intercom.Domain.Contracts;

namespace Intercom.Infrastructure.Audio;

public class WebRtcNoiseSuppressionService : INoiseFilterService
{
    public bool IsEnabled { get; set; } = true;
    public int SuppressiveLevelDb { get; set; } = 12;

    // High-pass filter for low-frequency wind and engine rumble at the 16 kHz voice rate.
    private const float AlphaHighPass = 0.93f;
    private const float NoiseFloor = 0.015f;
    private const float GateTransition = 0.025f;
    private float _lastInputSample = 0f;
    private float _lastOutputSample = 0f;

    public byte[] ProcessFrame(byte[] rawPcmData, int count)
    {
        if (!IsEnabled || SuppressiveLevelDb <= 0 || count <= 0)
        {
            return rawPcmData;
        }

        byte[] processed = new byte[count];
        int sampleCount = count / 2; // 16-bit PCM = 2 bytes per sample

        float quietGain = MathF.Pow(10, -Math.Clamp(SuppressiveLevelDb, 0, 30) / 20f);

        for (int i = 0; i < sampleCount; i++)
        {
            int byteIdx = i * 2;
            short pcmSample = (short)(rawPcmData[byteIdx] | (rawPcmData[byteIdx + 1] << 8));
            float floatSample = pcmSample / 32768.0f;

            // 1. High-Pass Filter (Wind & Engine Noise Attenuation)
            float filteredSample = AlphaHighPass * (_lastOutputSample + floatSample - _lastInputSample);
            _lastInputSample = floatSample;
            _lastOutputSample = filteredSample;

            // Fade out the gate as the signal rises above the noise floor.
            float open = Math.Clamp((Math.Abs(filteredSample) - NoiseFloor) / GateTransition, 0f, 1f);
            filteredSample *= quietGain + (1f - quietGain) * open;

            // 3. Audio Limiter (Soft Clamping)
            filteredSample = Math.Clamp(filteredSample, -1.0f, 1.0f);

            // Convert back to 16-bit PCM byte array
            short outputPcm = (short)(filteredSample * 32767.0f);
            processed[byteIdx] = (byte)(outputPcm & 0xFF);
            processed[byteIdx + 1] = (byte)((outputPcm >> 8) & 0xFF);
        }

        return processed;
    }
}
