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
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Application.Current.Dispatcher.BeginInvoke(activatePrimary),
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
