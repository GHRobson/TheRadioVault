using System.IO.Pipes;
using System.Text;

namespace TheRadioVault.Server.Services;

public sealed class ServerInstanceCoordinator : IDisposable
{
    private const string PipeName = "RadioVault.Server.Settings.v1";
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listener;
    private bool _disposed;

    private ServerInstanceCoordinator(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public bool IsPrimary => _ownsMutex;

    public static ServerInstanceCoordinator Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, "Local\\RadioVault.Server.Instance.v1", out var createdNew);
        return new ServerInstanceCoordinator(mutex, createdNew);
    }

    public void StartListening(Action showSettings)
    {
        ArgumentNullException.ThrowIfNull(showSettings);
        if (!IsPrimary || _listener is not null) return;
        _listener = Task.Run(() => ListenAsync(showSettings, _cancellation.Token));
    }

    public static async Task SignalPrimaryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes("show-settings"), timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // The first instance can still be starting. Its tray icon remains the fallback.
        }
    }

    private static async Task ListenAsync(Action showSettings, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[64];
                var count = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (Encoding.UTF8.GetString(buffer, 0, count).StartsWith("show-settings", StringComparison.Ordinal))
                    showSettings();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cancellation.Dispose();
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }
}
