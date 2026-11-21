namespace Intercom.Infrastructure.Audio;

public static class AudioGainAndLimiter
{
    public static byte[] ApplyGain(byte[] pcmFrame, int count, float gain)
    {
        if (Math.Abs(gain - 1.0f) < 0.01f || count <= 0)
            return pcmFrame;

        byte[] output = new byte[count];
        int sampleCount = count / 2;

        for (int i = 0; i < sampleCount; i++)
        {
            int byteIdx = i * 2;
            short pcmSample = (short)(pcmFrame[byteIdx] | (pcmFrame[byteIdx + 1] << 8));

            // Apply linear gain multiplier
            int scaledSample = (int)(pcmSample * gain);

            // Hard clamp to 16-bit PCM limits to prevent audio wrap-around distortion
            short clampedSample = (short)Math.Clamp(scaledSample, short.MinValue, short.MaxValue);

            output[byteIdx] = (byte)(clampedSample & 0xFF);
            output[byteIdx + 1] = (byte)((clampedSample >> 8) & 0xFF);
        }

        return output;
    }
}