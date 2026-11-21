using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Intercom.Application.Services;
using Intercom.Domain.Models;
using Intercom.Infrastructure.Network;

namespace Intercom.UI.ViewModels;

public sealed partial class IntercomViewModel : ObservableObject
{
    private const string NicknameKey = "rider-nickname";
    private readonly IntercomSessionManager _session;
    private bool _askingPermission;
    private string? _inputError;
    private bool _notificationDenied;
    private AudioSettings _audioSettings = new();

    [ObservableProperty]
    private string _nickname;

    [ObservableProperty]
    private string _hostAddress = "";

    [ObservableProperty]
    private bool _showAddressForm;

    [ObservableProperty]
    private bool _showAudioSettings;

    public IntercomViewModel(IntercomSessionManager session)
    {
        _session = session;
        _nickname = Preferences.Default.Get(NicknameKey, $"Rider {Random.Shared.Next(100, 1000)}");
        _session.Changed += () => MainThread.BeginInvokeOnMainThread(Refresh);
    }

    private SessionState State => _session.State;
    public bool IsLobby => State.Room is null;
    public bool IsInSession => !IsLobby;
    public bool IsBusy => _askingPermission || State.Operation != SessionOperation.None;
    public bool CanUseControls => !IsBusy;
    public bool IsScanning => State.IsScanning;
    public IReadOnlyList<PeerGroup> Groups => State.Groups;
    public IReadOnlyList<Rider> Riders => State.Riders;
    public string ScanButtonText => IsScanning ? "Stop scan" : "Scan nearby";
    public string LobbyStatus => State.Operation switch
    {
        SessionOperation.Hosting => "Opening your group…",
        SessionOperation.Joining => "Waiting for the host…",
        _ when _askingPermission => "Checking microphone permission…",
        _ when IsScanning && Groups.Count == 0 => "Scanning. No groups found yet.",
        _ when IsScanning => "Scanning for nearby groups…",
        _ => "Scan for a group or join by address."
    };

    public string? Error => _inputError ?? State.Error;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string RoomTitle => State.Room is { } room ? $"{room.HostNickname}’s group" : "";
    public string RoomAddress => State.Room is not { } room || string.IsNullOrEmpty(room.IpAddress)
        ? "Wi-Fi address unavailable"
        : room.IpAddress;
    public string RiderCount => Riders.Count == 1 ? "1 rider" : $"{Riders.Count} riders";
    public string SessionStatus => Riders.Count > 1 ? "Connected" : "Waiting for riders";
    public bool HandsFree => State.IsHandsFree;
    public bool ShowTalkButton => !HandsFree;
    public bool CanTalk => CanUseControls && !State.IsMuted && !HandsFree;
    public bool IsTransmitting => State.IsTransmitting;
    public bool ShowNotificationHint => HandsFree && _notificationDenied;
    public string TalkButtonText => IsTransmitting ? "Talking" : "Hold to talk";
    public string MuteButtonText => State.IsMuted ? "Unmute mic" : "Mute mic";
    public string MicrophoneStatus => State.IsMuted ? "Mic muted"
        : IsTransmitting ? "Mic live"
        : State.Operation == SessionOperation.ChangingMode ? "Changing audio mode…"
        : "Mic ready";

    public double InputGain
    {
        get => _audioSettings.InputGain;
        set => UpdateAudio(_audioSettings with { InputGain = (float)Math.Clamp(value, 0, 3) });
    }

    public double NoiseReduction
    {
        get => _audioSettings.NoiseReduction;
        set => UpdateAudio(_audioSettings with { NoiseReduction = (int)Math.Round(value) });
    }

    public bool ReduceNoise
    {
        get => _audioSettings.ReduceNoise;
        set => UpdateAudio(_audioSettings with { ReduceNoise = value });
    }

    public string GainLabel => $"Mic gain · {InputGain:0.0}×";
    public string NoiseLabel => $"Noise reduction · {NoiseReduction:0} dB";

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy)
            return;
        _inputError = null;
        if (IsScanning)
            await _session.StopScanningAsync();
        else
            await _session.ScanAsync();
        Refresh();
    }

    [RelayCommand]
    private Task HostAsync() => StartAsync(name =>
        _session.HostAsync(name, LanAddresses.HostAddresses().FirstOrDefault() ?? ""));

    [RelayCommand]
    private Task JoinAsync(PeerGroup? group) =>
        group is null ? Task.CompletedTask : StartAsync(name => _session.JoinAsync(name, group));

    [RelayCommand]
    private Task JoinAddressAsync()
    {
        if (!IPAddress.TryParse(HostAddress.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            HostAddress.Trim().Split('.').Length != 4)
        {
            _inputError = "Enter the host’s IPv4 address, for example 192.168.1.10.";
            Refresh();
            return Task.CompletedTask;
        }
        return JoinAsync(new PeerGroup(Guid.Empty, "", address.ToString(), IntercomSessionManager.SessionPort));
    }

    [RelayCommand]
    private void ToggleAddressForm() => ShowAddressForm = !ShowAddressForm;

    [RelayCommand]
    private void ToggleAudioSettings() => ShowAudioSettings = !ShowAudioSettings;

    [RelayCommand]
    private Task ToggleMuteAsync() => _session.SetMutedAsync(!State.IsMuted);

    [RelayCommand]
    private async Task LeaveAsync()
    {
        _inputError = null;
        await _session.LeaveAsync();
    }

    public Task HoldToTalkAsync(bool held) => _session.SetPushToTalkAsync(held);

    public async Task SetHandsFreeAsync(bool enabled)
    {
        if (IsBusy || !IsInSession || enabled == HandsFree)
            return;

        _askingPermission = true;
        _inputError = null;
        Refresh();
        try
        {
            if (enabled && OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                try
                {
                    var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
                    if (status != PermissionStatus.Granted)
                        status = await Permissions.RequestAsync<Permissions.PostNotifications>();
                    _notificationDenied = status != PermissionStatus.Granted;
                }
                catch (Exception error)
                {
                    System.Diagnostics.Debug.WriteLine(error);
                    _notificationDenied = true;
                }
            }
            await _session.SetHandsFreeAsync(enabled);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
            _inputError = "Couldn't change the audio mode. Try again.";
        }
        _askingPermission = false;
        Refresh();
    }

    private async Task StartAsync(Func<string, Task> start)
    {
        if (IsBusy || !IsLobby)
            return;

        var name = Nickname.Trim();
        if (name.Length is 0 or > IntercomSessionManager.MaxNicknameLength || name.Any(char.IsControl))
        {
            _inputError = "Enter a rider nickname of up to 24 characters.";
            Refresh();
            return;
        }

        _askingPermission = true;
        _inputError = null;
        try
        {
            Refresh();
            var permission = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (permission != PermissionStatus.Granted)
                permission = await Permissions.RequestAsync<Permissions.Microphone>();
            if (permission != PermissionStatus.Granted)
            {
                _inputError = "Allow microphone access in Android settings to host or join a group.";
            }
            else
            {
                Nickname = name;
                Preferences.Default.Set(NicknameKey, name);
                ShowAudioSettings = false;
                await start(name);
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
            _inputError = "Couldn't open the session. Check Android permissions and Wi-Fi, then try again.";
        }
        _askingPermission = false;
        Refresh();
    }

    private void UpdateAudio(AudioSettings settings)
    {
        if (settings == _audioSettings)
            return;
        _audioSettings = settings;
        _session.SetAudioSettings(settings);
        OnPropertyChanged(nameof(InputGain));
        OnPropertyChanged(nameof(NoiseReduction));
        OnPropertyChanged(nameof(ReduceNoise));
        OnPropertyChanged(nameof(GainLabel));
        OnPropertyChanged(nameof(NoiseLabel));
    }

    private void Refresh()
    {
        // All of the derived display values come from the same immutable session snapshot.
        OnPropertyChanged(string.Empty);
    }
}

