namespace Intercom.Domain.Contracts;

public interface INoiseFilterService
{
    bool IsEnabled { get; set; }
    int SuppressiveLevelDb { get; set; }

    /// <summary>
    /// Processes standard 16-bit PCM audio frame (8kHz or 16kHz mono).
    /// </summary>
    byte[] ProcessFrame(byte[] rawPcmData, int count);
}