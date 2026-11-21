using Intercom.Application.Services;
using Intercom.Domain.Contracts;
using Intercom.Domain.Models;

namespace Intercom.Tests;

public sealed class SessionTests
{
    [Fact]
    public async Task SessionCommandsCanFinishWhileAnObserverAppliesThePreviousState()
    {
        var session = new Fixture().Session;
        using var releaseObserver = new ManualResetEventSlim();
        var observerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            observerStarted.TrySetResult();
            releaseObserver.Wait();
            observerFinished.TrySetResult();
        }

        session.Changed += OnChanged;
        try
        {
            var timeout = TimeSpan.FromSeconds(4);
            await Task.Run(session.ScanAsync).WaitAsync(timeout);
            await observerStarted.Task.WaitAsync(timeout);
            session.Changed -= OnChanged;
            await session.StopScanningAsync().WaitAsync(timeout);
            Assert.False(session.State.IsScanning);
        }
        finally
        {
            releaseObserver.Set();
            if (observerStarted.Task.IsCompleted)
                await observerFinished.Task.WaitAsync(TimeSpan.FromSeconds(4));
        }
    }

    [Fact]
    public async Task PushToTalkAndMuteGateCaptureAndSending()
    {
        var fixture = new Fixture();
        var session = fixture.Session;
        await session.HostAsync("Host", "127.0.0.1");
        Assert.False(fixture.Audio.Capturing);
        await session.SetPushToTalkAsync(true);
        Assert.True(fixture.Audio.Capturing);
        fixture.Audio.Emit();
        await NetworkTests.EventuallyAsync(() => fixture.Network.Sent == 1);

        await session.SetPushToTalkAsync(false);
        Assert.False(fixture.Audio.Capturing);
        fixture.Audio.Emit();
        Assert.Equal(1, fixture.Network.Sent);

        await session.SetMutedAsync(true);
        await session.SetPushToTalkAsync(true);
        Assert.False(fixture.Audio.Capturing);
        fixture.Network.Receive();
        Assert.Equal(1, fixture.Audio.Played);
        fixture.Audio.Emit();
        Assert.Equal(1, fixture.Network.Sent);

        await session.SetMutedAsync(false);
        Assert.False(fixture.Audio.Capturing);
        await session.LeaveAsync();
        Assert.False(fixture.Audio.Playing);
    }

    [Fact]
    public async Task HandsFreeSurvivesBackgroundAndMuteStopsCapture()
    {
        var fixture = new Fixture();
        var session = fixture.Session;
        await session.HostAsync("Host", "127.0.0.1");
        fixture.Audio.BeforeCapture = () => Assert.True(fixture.Background.Running);
        await session.SetHandsFreeAsync(true);
        session.OnForegroundChanged(false);
        await session.OnStoppedAsync();

        Assert.True(session.State.IsHandsFree);
        Assert.True(fixture.Audio.Capturing);
        fixture.Audio.Emit();
        await NetworkTests.EventuallyAsync(() => fixture.Network.Sent == 1);

        await session.SetMutedAsync(true);
        Assert.False(fixture.Audio.Capturing);
        Assert.True(fixture.Audio.Playing);
        Assert.True(fixture.Background.Running);
        fixture.Network.Receive();
        Assert.Equal(1, fixture.Audio.Played);
        await session.SetMutedAsync(false);
        Assert.True(fixture.Audio.Capturing);

        fixture.Background.RequestEnd();
        await NetworkTests.EventuallyAsync(() => session.State.Room is null);
        Assert.False(fixture.Audio.Capturing);
        Assert.False(fixture.Background.Running);
        Assert.False(fixture.Discovery.Running);
    }

    [Fact]
    public async Task FailedBackgroundServiceKeepsPushToTalkAndLeavingIsRepeatable()
    {
        var fixture = new Fixture();
        var session = fixture.Session;
        fixture.Background.Fail = true;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            session.OnForegroundChanged(true);
            await session.HostAsync("Host", "127.0.0.1");
            await session.SetHandsFreeAsync(true);
            Assert.False(session.State.IsHandsFree);
            Assert.Contains("Hold to talk is still available", session.State.Error);
            await session.SetPushToTalkAsync(true);
            Assert.True(fixture.Audio.Capturing);

            session.OnForegroundChanged(false);
            await session.OnStoppedAsync();
            Assert.Null(session.State.Room);
            Assert.False(fixture.Audio.Capturing);
            Assert.False(fixture.Audio.Playing);
            Assert.False(fixture.Network.Active);
            Assert.False(fixture.Discovery.Running);
        }
    }

    [Fact]
    public async Task TransportFailureReturnsToLobbyWithRetryMessage()
    {
        var fixture = new Fixture();
        var session = fixture.Session;
        await session.JoinAsync("Guest", new PeerGroup(Guid.NewGuid(), "Host", "127.0.0.1", 50005));
        fixture.Network.Fail();
        await NetworkTests.EventuallyAsync(() => session.State.Room is null);
        Assert.Equal("The host ended the group.", session.State.Error);
        Assert.False(fixture.Audio.Playing);

        await session.HostAsync("Guest", "127.0.0.1");
        Assert.NotNull(session.State.Room);
        await session.LeaveAsync();
    }

    private sealed class Fixture
    {
        public readonly FakeAudio Audio = new();
        public readonly FakeTransport Network = new();
        public readonly FakeDiscovery Discovery = new();
        public readonly FakeBackground Background = new();
        public IntercomSessionManager Session { get; }

        public Fixture() => Session = new IntercomSessionManager(Audio, Network, Discovery, Background);
    }

    private sealed class FakeAudio : IAudioEngine
    {
        public event Action<byte[]>? FrameCaptured;
        public event Action<Exception>? Failed { add { } remove { } }
        public Action? BeforeCapture;
        public bool Capturing;
        public bool Playing;
        public int Played;
        public void StartPlayback() => Playing = true;
        public void StartCapture()
        {
            BeforeCapture?.Invoke();
            Capturing = true;
        }
        public Task StopCaptureAsync()
        {
            Capturing = false;
            return Task.CompletedTask;
        }
        public Task StopPlaybackAsync()
        {
            Playing = false;
            return Task.CompletedTask;
        }
        public void QueuePlayback(Guid speaker, byte[] pcm) => Played++;
        public void SetSettings(AudioSettings settings) { }
        public void Emit() => FrameCaptured?.Invoke(new byte[VoiceFrame.Size]);
    }

    private sealed class FakeTransport : IIntercomTransport
    {
        private int _sent;
        public int Sent => Volatile.Read(ref _sent);
        public bool Active;
        public event Action<IReadOnlyList<Rider>>? RidersChanged { add { } remove { } }
        public event Action<Guid, byte[]>? VoiceReceived;
        public event Action<Exception>? ConnectionLost;
        public IReadOnlyList<Rider> Riders { get; private set; } = [];
        public Task HostAsync(Room room, Rider local, CancellationToken cancellationToken)
        {
            Assert.False(Active);
            Active = true;
            Riders = [local];
            return Task.CompletedTask;
        }
        public async Task<Room> JoinAsync(PeerGroup group, Rider local, CancellationToken cancellationToken)
        {
            var room = new Room(group.Id, group.HostNickname, group.IpAddress, group.Port, false);
            await HostAsync(room, local, cancellationToken);
            return room;
        }
        public ValueTask SendVoiceAsync(byte[] pcm, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sent);
            return ValueTask.CompletedTask;
        }
        public Task StopAsync()
        {
            Active = false;
            Riders = [];
            return Task.CompletedTask;
        }
        public void Receive() => VoiceReceived?.Invoke(Guid.NewGuid(), new byte[VoiceFrame.Size]);
        public void Fail() => ConnectionLost?.Invoke(new IOException("The host ended the group."));
    }

    private sealed class FakeDiscovery : INetworkDiscoveryService
    {
        public event Action<IReadOnlyList<PeerGroup>>? GroupsChanged { add { } remove { } }
        public event Action<Exception>? Failed { add { } remove { } }
        public bool Running;
        public Task ListenAsync() => Task.CompletedTask;
        public Task AdvertiseAsync(Room room)
        {
            Running = true;
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            Running = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBackground : IBackgroundSession
    {
        public event Action? EndRequested;
        public bool Running;
        public bool Fail;
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (Fail)
                throw new InvalidOperationException("Foreground service failure");
            Running = true;
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            Running = false;
            return Task.CompletedTask;
        }
        public void RequestEnd() => EndRequested?.Invoke();
    }
}

