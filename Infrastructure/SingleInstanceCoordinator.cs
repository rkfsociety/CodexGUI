using System;
using System.Threading;
using System.Windows;

namespace CodexGUI;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\CodexGUI.SingleInstance";
    private const string ActivationEventName = @"Local\CodexGUI.ActivatePrimary";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private RegisteredWaitHandle? _activationWait;

    private SingleInstanceCoordinator(Mutex mutex, EventWaitHandle activationEvent)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
    }

    public static SingleInstanceCoordinator? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            SignalPrimaryInstance();
            return null;
        }

        try
        {
            return new SingleInstanceCoordinator(
                mutex,
                new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivationEventName));
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void ListenForActivation(Action activatePrimary)
    {
        ArgumentNullException.ThrowIfNull(activatePrimary);
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("Диспетчер приложения недоступен.");

        // Re-registering must not leak a previous wait callback.
        _activationWait?.Unregister(null);
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) =>
            {
                // A second launch can signal us while the primary application is closing.
                // Posting to a stopped dispatcher must not crash a ThreadPool callback.
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                    return;
                try
                {
                    dispatcher.BeginInvoke(activatePrimary);
                }
                catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                {
                    // The dispatcher shut down between the check and the post.
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _activationWait?.Unregister(null);
        _activationEvent.Dispose();
        _mutex.Dispose();
    }

    internal static void SignalPrimaryInstance()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
