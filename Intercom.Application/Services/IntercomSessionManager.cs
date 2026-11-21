using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Intercom.Domain.Contracts;
using Intercom.Domain.Models;

namespace Intercom.Application.Services;

public enum SessionOperation { None, Hosting, Joining, ChangingMode, Leaving }

public sealed record SessionState
{
    public Room? Room { get; init; }
    public IReadOnlyList<Rider> Riders { get; init; } = [];
    public IReadOnlyList<PeerGroup> Groups { get; init; } = [];
    public bool IsScanning { get; init; }
    public bool IsHandsFree { get; init; }
    public bool IsMuted { get; init; }
    public bool IsTransmitting { get; init; }
    public SessionOperation Operation { get; init; }
    public string? Error { get; init; }
}

public sealed class IntercomSessionManager
{
    public const int SessionPort = 50005;
    public const int MaxNicknameLength = 24;

    private readonly IAudioEngine _audio;
    private readonly IIntercomTransport _transport;
    private readonly INetworkDiscoveryService _discovery;
    private readonly IBackgroundSession _background;
    private readonly SemaphoreSlim _gate = new(1);
    private SessionState _state = new();
    private IReadOnlyList<PeerGroup> _latestGroups = [];
    private Transmission? _transmission;
    private volatile bool _foreground = true;
    private volatile bool _pttHeld;
    private int _generation;

    public SessionState State => Volatile.Read(ref _state);
    public event Action? Changed;

    public IntercomSessionManager(IAudioEngine audio, IIntercomTransport transport,
        INetworkDiscoveryService discovery, IBackgroundSession background)
    {
        _audio = audio;
        _transport = transport;
        _discovery = discovery;
        _background = background;

        _audio.FrameCaptured += OnFrameCaptured;
        _audio.Failed += error => SignalFault(error, "Audio stopped. Check the microphone and try again.");
        _transport.VoiceReceived += (speaker, pcm) =>
        {
            if (State.Room is not null)
                _audio.QueuePlayback(speaker, pcm);
        };
        _transport.RidersChanged += _ => Schedule(() =>
        {
            if (State.Room is not null)
                Publish(State with { Riders = _transport.Riders });
            return Task.CompletedTask;
        });
        _transport.ConnectionLost += error => SignalFault(error, error is IOException
            ? error.Message
            : "The Wi-Fi connection was lost. Try joining again.");
        _discovery.GroupsChanged += groups =>
        {
            Volatile.Write(ref _latestGroups, groups);
            Schedule(() =>
            {
                if (State.IsScanning)
                    Publish(State with { Groups = Volatile.Read(ref _latestGroups) });
                return Task.CompletedTask;
            });
        };
        _discovery.Failed += error => Schedule(async () =>
        {
            Log(error);
            await _discovery.StopAsync();
            Publish(State.IsScanning
                ? State with { IsScanning = false, Groups = [], Error = "Scanning stopped. Try scanning again or join by address." }
                : State with { Error = "Nearby discovery stopped. Riders can still join by address." });
        });
        _background.EndRequested += () => Schedule(() => LeaveCoreAsync());
    }

    public Task ScanAsync() => SerializeAsync(async () =>
    {
        if (State.Room is not null || State.IsScanning)
            return;

        AdvanceGeneration();
        Volatile.Write(ref _latestGroups, []);
        try
        {
            await _discovery.ListenAsync();
            Publish(State with { IsScanning = true, Groups = [], Error = null });
        }
        catch (Exception error)
        {
            Log(error);
            Publish(State with { Error = "Couldn't scan for groups. Check Wi-Fi or try joining by address." });
        }
    });

    public Task StopScanningAsync() => SerializeAsync(async () =>
    {
        if (!State.IsScanning)
            return;

        AdvanceGeneration();
        await _discovery.StopAsync();
        Publish(State with { IsScanning = false, Groups = [] });
    });

    public Task HostAsync(string nickname, string hostAddress) => StartAsync(nickname, hostAddress, null);

    public Task JoinAsync(string nickname, PeerGroup group) => StartAsync(nickname, null, group);

    public Task LeaveAsync() => SerializeAsync(() => LeaveCoreAsync());

    public Task SetHandsFreeAsync(bool enabled) => SerializeAsync(async () =>
    {
        if (State.Room is null || State.IsHandsFree == enabled)
            return;

        _pttHeld = false;
        Publish(State with { Operation = SessionOperation.ChangingMode, Error = null });
        await EndTransmissionAsync();

        if (!enabled)
        {
            Publish(State with { IsHandsFree = false, Operation = SessionOperation.None });
            await CleanupAsync(_background.StopAsync);
            if (!_foreground)
                await LeaveCoreAsync();
            return;
        }

        try
        {
            if (!_foreground)
                throw new InvalidOperationException("The app must be visible to start hands-free audio.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await _background.StartAsync(timeout.Token);
        }
        catch (Exception error)
        {
            Log(error);
            await CleanupAsync(_background.StopAsync);
            Publish(State with { Operation = SessionOperation.None, Error = "Couldn't start Hands-free. Hold to talk is still available." });
            return;
        }

        Publish(State with { IsHandsFree = true, Operation = SessionOperation.None });
        await RefreshTransmissionAsync();
    });

    public Task SetMutedAsync(bool muted) => SerializeAsync(async () =>
    {
        if (State.Room is null || State.IsMuted == muted)
            return;
        _pttHeld = false;
        Publish(State with { IsMuted = muted });
        await RefreshTransmissionAsync();
    });

    public Task SetPushToTalkAsync(bool held)
    {
        _pttHeld = held && _foreground && State is { Room: not null, IsHandsFree: false, IsMuted: false };
        return SerializeAsync(RefreshTransmissionAsync);
    }

    public void OnForegroundChanged(bool foreground)
    {
        _foreground = foreground;
        if (!foreground)
        {
            _pttHeld = false;
            Schedule(RefreshTransmissionAsync);
        }
    }

    public Task OnStoppedAsync() => SerializeAsync(async () =>
    {
        if (_foreground)
            return;
        if (State.Room is not null && !State.IsHandsFree)
            await LeaveCoreAsync();
        else if (State.IsScanning)
        {
            AdvanceGeneration();
            await _discovery.StopAsync();
            Publish(State with { IsScanning = false, Groups = [] });
        }
    });

    public void SetAudioSettings(AudioSettings settings) => _audio.SetSettings(settings);

    private Task StartAsync(string nickname, string? hostAddress, PeerGroup? group) => SerializeAsync(async () =>
    {
        if (State.Room is not null || !_foreground)
            return;
        nickname = nickname.Trim();
        if (nickname.Length is 0 or > MaxNicknameLength || nickname.Any(char.IsControl))
        {
            Publish(State with { Error = "Enter a rider nickname of up to 24 characters." });
            return;
        }
        if (group is not null && (!IPAddress.TryParse(group.IpAddress, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork || group.Port is <= 0 or > 65535))
        {
            Publish(State with { Error = "Enter the host's IPv4 address, for example 192.168.1.10." });
            return;
        }

        AdvanceGeneration();
        Publish(new SessionState { Operation = group is null ? SessionOperation.Hosting : SessionOperation.Joining });
        var local = new Rider(Guid.NewGuid(), nickname);
        try
        {
            await _discovery.StopAsync();
            Room room;
            if (group is null)
            {
                room = new Room(Guid.NewGuid(), nickname, hostAddress ?? "", SessionPort, true);
                await _transport.HostAsync(room, local, CancellationToken.None);
                await _discovery.AdvertiseAsync(room);
            }
            else
            {
                room = await _transport.JoinAsync(group, local, CancellationToken.None);
            }

            if (!_foreground)
            {
                await LeaveCoreAsync();
                return;
            }
            _audio.StartPlayback();
            Publish(new SessionState { Room = room, Riders = _transport.Riders });
        }
        catch (Exception error)
        {
            Log(error);
            var message = error is TimeoutException || error is InvalidOperationException { Message: "This group is full." }
                ? error.Message
                : group is null
                    ? "Couldn't host the group. Check Wi-Fi and microphone permission, then try again."
                    : "Couldn't join the group. Check Wi-Fi and microphone permission, then try again.";
            await LeaveCoreAsync(message);
        }
    });

    private async Task RefreshTransmissionAsync()
    {
        var state = State;
        if (!ShouldTransmit(state))
        {
            await EndTransmissionAsync();
            return;
        }
        if (_transmission is not null)
            return;

        var transmission = new Transmission();
        Volatile.Write(ref _transmission, transmission);
        transmission.Worker = Task.Run(() => SendFramesAsync(transmission));
        try
        {
            _audio.StartCapture();
            Publish(State with { IsTransmitting = true, Error = null });
        }
        catch (Exception error)
        {
            Log(error);
            _pttHeld = false;
            Publish(State with { IsHandsFree = false, Error = "Couldn't open the microphone. Check permission and try again." });
            await EndTransmissionAsync();
            if (state.IsHandsFree)
                await CleanupAsync(_background.StopAsync);
            if (!_foreground)
                await LeaveCoreAsync(State.Error);
        }
    }

    private bool ShouldTransmit(SessionState state) =>
        state.Room is not null && !state.IsMuted &&
        state.Operation == SessionOperation.None && (state.IsHandsFree || (_foreground && _pttHeld));

    private void OnFrameCaptured(byte[] pcm)
    {
        var transmission = Volatile.Read(ref _transmission);
        if (transmission is not null && ShouldTransmit(State) && pcm.Length == VoiceFrame.Size)
            transmission.Frames.Writer.TryWrite(pcm);
    }

    private async Task SendFramesAsync(Transmission transmission)
    {
        var token = transmission.Lifetime.Token;
        try
        {
            while (await transmission.Frames.Reader.WaitToReadAsync(token))
            {
                while (!token.IsCancellationRequested && transmission.Frames.Reader.TryRead(out var frame) &&
                    ShouldTransmit(State))
                    await _transport.SendVoiceAsync(frame, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested)
                SignalFault(error, "Voice transmission stopped. Try reconnecting.");
        }
    }

    private async Task EndTransmissionAsync()
    {
        var transmission = Interlocked.Exchange(ref _transmission, null);
        if (transmission is null)
            return;

        transmission.Lifetime.Cancel();
        transmission.Frames.Writer.TryComplete();
        if (State.IsTransmitting)
            Publish(State with { IsTransmitting = false });
        await CleanupAsync(_audio.StopCaptureAsync);
        await CleanupAsync(() => transmission.Worker.WaitAsync(TimeSpan.FromSeconds(2)));
        transmission.Lifetime.Dispose();
    }

    private async Task LeaveCoreAsync(string? error = null)
    {
        AdvanceGeneration();
        _pttHeld = false;
        Publish(State with { Operation = SessionOperation.Leaving });
        await EndTransmissionAsync();
        await CleanupAsync(_background.StopAsync);
        await CleanupAsync(_discovery.StopAsync);
        await CleanupAsync(_transport.StopAsync);
        await CleanupAsync(_audio.StopPlaybackAsync);
        AdvanceGeneration();
        Publish(new SessionState { Error = error });
    }

    private static async Task CleanupAsync(Func<Task> stop)
    {
        try { await stop(); }
        catch (Exception error) { Log(error); }
    }

    private void SignalFault(Exception error, string message) => Schedule(async () =>
    {
        Log(error);
        if (State.Room is not null)
            await LeaveCoreAsync(message);
    });

    private void Schedule(Func<Task> operation)
    {
        var generation = Volatile.Read(ref _generation);
        _ = Task.Run(() => SerializeAsync(async () =>
        {
            if (generation == Volatile.Read(ref _generation))
                await operation();
        }));
    }

    private async Task SerializeAsync(Func<Task> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await operation().ConfigureAwait(false); }
        catch
        {
            _gate.Release();
            throw;
        }
        _gate.Release();
    }

    private void AdvanceGeneration() => Interlocked.Increment(ref _generation);

    private void Publish(SessionState state)
    {
        Volatile.Write(ref _state, state);
        // A UI observer can request another operation while applying the new state.
        if (Changed is { } changed)
            _ = Task.Run(changed);
    }

    private static void Log(Exception error) => System.Diagnostics.Debug.WriteLine(error);

    private sealed class Transmission
    {
        public readonly Channel<byte[]> Frames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(4) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        public readonly CancellationTokenSource Lifetime = new();
        public Task Worker = Task.CompletedTask;
    }
}

