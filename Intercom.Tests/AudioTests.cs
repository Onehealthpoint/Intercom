using System.Buffers.Binary;
using Intercom.Domain.Models;
using Intercom.Infrastructure.Audio;

namespace Intercom.Tests;

public sealed class AudioTests
{
    [Fact]
    public void NoiseReductionAttenuatesQuietPcmAndCanBeDisabled()
    {
        var quiet = new byte[VoiceFrame.Size];
        BinaryPrimitives.WriteInt16LittleEndian(quiet, 300);
        var bypass = new WebRtcNoiseSuppressionService { IsEnabled = false };
        var light = new WebRtcNoiseSuppressionService { SuppressiveLevelDb = 6 };
        var strong = new WebRtcNoiseSuppressionService { SuppressiveLevelDb = 24 };

        Assert.Equal(quiet, bypass.ProcessFrame(quiet, quiet.Length));
        Assert.InRange(FirstSample(light), 130, 155);
        Assert.InRange(FirstSample(strong), 10, 25);

        short FirstSample(WebRtcNoiseSuppressionService filter) =>
            BinaryPrimitives.ReadInt16LittleEndian(filter.ProcessFrame(quiet, quiet.Length));
    }
}
