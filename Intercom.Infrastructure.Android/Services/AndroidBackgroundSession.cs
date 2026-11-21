using Android.Content;
using Intercom.Domain.Contracts;

namespace Intercom.Infrastructure.Android.Services;

public sealed class AndroidBackgroundSession : IBackgroundSession
{
    private readonly object _sync = new();
    private TaskCompletionSource? _started;
    private int _sessionId;

    public static AndroidBackgroundSession Instance { get; } = new();
    private AndroidBackgroundSession() { }

    public event Action? EndRequested;

    private static Context Context => global::Android.App.Application.Context;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Task started;
        int id;
        lock (_sync)
        {
            _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            started = _started.Task;
            id = ++_sessionId;
        }

        using var intent = CreateIntent(id);
        using var service = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? Context.StartForegroundService(intent)
            : Context.StartService(intent);
        if (service is null)
            throw new InvalidOperationException("Android couldn't start the intercom service.");

        return started.WaitAsync(cancellationToken);
    }

    public Task StopAsync()
    {
        lock (_sync)
        {
            if (_started is null)
                return Task.CompletedTask;
            _started.TrySetCanceled();
            _started = null;
        }

        using var intent = new Intent(Context, typeof(IntercomForegroundService));
        Context.StopService(intent);
        return Task.CompletedTask;
    }

    internal static Intent CreateIntent(int sessionId) =>
        new Intent(Context, typeof(IntercomForegroundService)).PutExtra(IntercomForegroundService.SessionExtra, sessionId)!;

    internal bool IsCurrent(int id)
    {
        lock (_sync)
            return _started is not null && _sessionId == id;
    }

    internal void Started(int id)
    {
        lock (_sync)
        {
            if (id == _sessionId)
                _started?.TrySetResult();
        }
    }

    internal void StartFailed(int id, Exception error)
    {
        lock (_sync)
        {
            if (id == _sessionId)
                _started?.TrySetException(error);
        }
    }

    internal void ServiceEnded(int id)
    {
        lock (_sync)
        {
            if (_started is null || _sessionId != id)
                return;
            if (!_started.Task.IsCompletedSuccessfully)
            {
                _started.TrySetException(new InvalidOperationException("The intercom service ended before it was ready."));
                return;
            }
            _started = null;
        }
        EndRequested?.Invoke();
    }
}

