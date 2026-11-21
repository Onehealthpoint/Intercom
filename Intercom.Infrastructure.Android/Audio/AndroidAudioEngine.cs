using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using Android.Media;
using Intercom.Domain.Contracts;
using Intercom.Domain.Models;
using Intercom.Infrastructure.Audio;

namespace Intercom.Infrastructure.Android.Audio;

public sealed class AndroidAudioEngine(INoiseFilterService noiseFilter) : IAudioEngine
{
    private readonly object _playbackLock = new();
    private readonly Dictionary<Guid, Queue<byte[]>> _playbackFrames = new();
    private AudioManager? _audioManager;
    private Mode _previousMode;
    private AudioRecord? _record;
    private AudioTrack? _track;
    private CancellationTokenSource? _capture;
    private CancellationTokenSource? _playback;
    private Task? _captureWorker;
    private Task? _playbackWorker;
    private AudioSettings _settings = new();

    public event Action<byte[]>? FrameCaptured;
    public event Action<Exception>? Failed;

    public void SetSettings(AudioSettings settings) => Volatile.Write(ref _settings, settings);

    public void StartPlayback()
    {
        if (_track is not null)
            return;

        var manager = (AudioManager?)global::Android.App.Application.Context
            .GetSystemService(global::Android.Content.Context.AudioService);
        var minimum = AudioTrack.GetMinBufferSize(VoiceFrame.SampleRate, ChannelOut.Mono, Encoding.Pcm16bit);
        if (minimum <= 0)
            throw new InvalidOperationException("Voice playback is unavailable.");

        var size = Math.Max(minimum, VoiceFrame.Size * 2);
        var track = CreateTrack(size);
        try
        {
            if (track.State != AudioTrackState.Initialized)
                throw new InvalidOperationException("Android couldn't initialize voice playback.");

            _audioManager = manager;
            if (manager is not null)
            {
                _previousMode = manager.Mode;
                manager.Mode = Mode.InCommunication;
            }
            track.Play();
            _playback = new CancellationTokenSource();
            lock (_playbackLock)
                _track = track;
            var token = _playback.Token;
            _playbackWorker = Task.Run(() => PlayAsync(track, token));
        }
        catch
        {
            try { ReleaseTrack(track); }
            finally { RestoreAudioMode(); }
            throw;
        }
    }

    private static AudioTrack CreateTrack(int size)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            using var attributesBuilder = new AudioAttributes.Builder();
            using var attributes = attributesBuilder.SetUsage(AudioUsageKind.VoiceCommunication)!
                .SetContentType(AudioContentType.Speech)!.Build()!;
            using var formatBuilder = new AudioFormat.Builder();
            using var format = formatBuilder.SetEncoding(Encoding.Pcm16bit)!
                .SetSampleRate(VoiceFrame.SampleRate)!.SetChannelMask(ChannelOut.Mono)!.Build()!;
            using var builder = new AudioTrack.Builder();
            builder.SetAudioAttributes(attributes);
            builder.SetAudioFormat(format);
            builder.SetBufferSizeInBytes(size);
            builder.SetTransferMode(AudioTrackMode.Stream);
            return builder.Build()!;
        }

#pragma warning disable CS0618
        return new AudioTrack(global::Android.Media.Stream.VoiceCall, VoiceFrame.SampleRate, ChannelOut.Mono,
            Encoding.Pcm16bit, size, AudioTrackMode.Stream);
#pragma warning restore CS0618
    }

    public void StartCapture()
    {
        if (_record is not null)
            return;

        var minimum = AudioRecord.GetMinBufferSize(VoiceFrame.SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        if (minimum <= 0)
            throw new InvalidOperationException("Microphone capture is unavailable.");

#pragma warning disable CS0618
        var record = new AudioRecord(AudioSource.VoiceCommunication, VoiceFrame.SampleRate, ChannelIn.Mono,
            Encoding.Pcm16bit, Math.Max(minimum, VoiceFrame.Size * 2));
#pragma warning restore CS0618
        try
        {
            if (record.State != State.Initialized)
                throw new InvalidOperationException("Android couldn't initialize the microphone.");
            record.StartRecording();
            if (record.RecordingState != RecordState.Recording)
                throw new InvalidOperationException("Android couldn't start the microphone.");

            _capture = new CancellationTokenSource();
            _record = record;
            var token = _capture.Token;
            _captureWorker = Task.Run(() => Capture(record, token));
        }
        catch
        {
            record.Release();
            record.Dispose();
            throw;
        }
    }

    private void Capture(AudioRecord record, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = new byte[VoiceFrame.Size];
                var offset = 0;
                while (offset < frame.Length && !token.IsCancellationRequested)
                {
                    var count = record.Read(frame, offset, frame.Length - offset);
                    if (count <= 0)
                        throw new IOException($"Android microphone read failed ({count}).");
                    offset += count;
                }

                if (token.IsCancellationRequested)
                    break;

                var settings = Volatile.Read(ref _settings);
                noiseFilter.IsEnabled = settings.ReduceNoise;
                noiseFilter.SuppressiveLevelDb = settings.NoiseReduction;
                var filtered = noiseFilter.ProcessFrame(frame, frame.Length);
                FrameCaptured?.Invoke(AudioGainAndLimiter.ApplyGain(filtered, filtered.Length, settings.InputGain));
            }
        }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested)
                Failed?.Invoke(error);
        }
    }

    public Task StopCaptureAsync()
    {
        if (_record is not { } record)
            return Task.CompletedTask;

        var lifetime = _capture!;
        var worker = _captureWorker;
        _record = null;
        _capture = null;
        _captureWorker = null;
        return StopWorkerAsync(lifetime, worker, () =>
            {
                if (record.RecordingState == RecordState.Recording)
                    record.Stop();
            },
            () =>
            {
                try { record.Release(); }
                finally { record.Dispose(); }
            });
    }

    public void QueuePlayback(Guid speaker, byte[] pcm)
    {
        if (pcm.Length != VoiceFrame.Size)
            return;

        lock (_playbackLock)
        {
            if (_track is null)
                return;
            if (!_playbackFrames.TryGetValue(speaker, out var frames))
            {
                frames = new Queue<byte[]>(4);
                _playbackFrames.Add(speaker, frames);
            }
            if (frames.Count == 4)
                frames.Dequeue();
            frames.Enqueue(pcm);
        }
    }

    private async Task PlayAsync(AudioTrack track, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(VoiceFrame.Milliseconds));
        var output = new byte[VoiceFrame.Size];
        var samples = new int[VoiceFrame.Size / sizeof(short)];
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                byte[][] frames;
                lock (_playbackLock)
                {
                    if (_playbackFrames.Count == 0)
                        continue;
                    frames = _playbackFrames.Values.Select(queue => queue.Dequeue()).ToArray();
                    foreach (var speaker in _playbackFrames.Where(entry => entry.Value.Count == 0).Select(entry => entry.Key).ToArray())
                        _playbackFrames.Remove(speaker);
                }

                var frame = frames.Length == 1 ? frames[0] : Mix(frames, output, samples);
                var offset = 0;
                while (offset < frame.Length && !token.IsCancellationRequested)
                {
                    var written = track.Write(frame, offset, frame.Length - offset);
                    if (written <= 0)
                        throw new IOException($"Android playback write failed ({written}).");
                    offset += written;
                }
            }
        }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested)
                Failed?.Invoke(error);
        }
    }

    private static byte[] Mix(byte[][] frames, byte[] output, int[] samples)
    {
        Array.Clear(samples);
        foreach (var frame in frames)
        {
            for (var index = 0; index < samples.Length; index++)
                samples[index] += BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(index * sizeof(short)));
        }

        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(index * sizeof(short)),
                (short)Math.Clamp(samples[index], short.MinValue, short.MaxValue));
        }
        return output;
    }

    public Task StopPlaybackAsync()
    {
        AudioTrack? track;
        lock (_playbackLock)
        {
            track = _track;
            _track = null;
            _playbackFrames.Clear();
        }
        if (track is null)
            return Task.CompletedTask;

        var lifetime = _playback!;
        var worker = _playbackWorker;
        _playback = null;
        _playbackWorker = null;
        return StopWorkerAsync(lifetime, worker, () =>
            {
                track.Pause();
                track.Flush();
            },
            () =>
            {
                try { ReleaseTrack(track); }
                finally { RestoreAudioMode(); }
            });
    }

    private static async Task StopWorkerAsync(CancellationTokenSource lifetime, Task? worker, Action stop, Action release)
    {
        lifetime.Cancel();
        Exception? failure = null;
        try { stop(); }
        catch (Exception error) { failure = error; }
        try
        {
            if (worker is not null)
                await worker.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception error) { failure ??= error; }
        lifetime.Dispose();
        release();
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static void ReleaseTrack(AudioTrack track)
    {
        try { track.Release(); }
        finally { track.Dispose(); }
    }

    private void RestoreAudioMode()
    {
        if (_audioManager is { Mode: Mode.InCommunication } manager)
            manager.Mode = _previousMode;
        _audioManager = null;
    }
}

