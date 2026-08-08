using System.Diagnostics;
using System.Runtime.InteropServices;
using TheRadioVault.Transcription.Contracts;

namespace TheRadioVault.Desktop.Avalonia.Transcription;

/// <summary>Pauses the native Whisper worker without terminating or restarting its work.</summary>
public sealed class WindowsTranscriptionProcessController : ITranscriptionProcessController
{
    private const uint SuspendResume = 0x0002;
    private readonly object _gate = new();
    private readonly HashSet<int> _pausedProcesses = new();

    public bool TryPause(int processId)
    {
        lock (_gate)
        {
            if (_pausedProcesses.Contains(processId)) return true;
            var suspended = new List<int>();
            try
            {
                using var process = Process.GetProcessById(processId);
                foreach (ProcessThread thread in process.Threads)
                {
                    var handle = OpenThread(SuspendResume, false, (uint)thread.Id);
                    if (handle == IntPtr.Zero || SuspendThread(handle) == uint.MaxValue)
                    {
                        if (handle != IntPtr.Zero) CloseHandle(handle);
                        ResumeThreads(suspended);
                        return false;
                    }
                    CloseHandle(handle);
                    suspended.Add(thread.Id);
                }
                _pausedProcesses.Add(processId);
                return true;
            }
            catch
            {
                ResumeThreads(suspended);
                return false;
            }
        }
    }

    public bool TryResume(int processId)
    {
        lock (_gate)
        {
            if (!_pausedProcesses.Remove(processId)) return false;
            try
            {
                using var process = Process.GetProcessById(processId);
                return ResumeThreads(process.Threads.Cast<ProcessThread>().Select(x => x.Id));
            }
            catch
            {
                return true; // An exited worker no longer needs resuming.
            }
        }
    }

    private static bool ResumeThreads(IEnumerable<int> threadIds)
    {
        var success = true;
        foreach (var threadId in threadIds)
        {
            var handle = OpenThread(SuspendResume, false, (uint)threadId);
            if (handle == IntPtr.Zero) { success = false; continue; }
            if (ResumeThread(handle) == uint.MaxValue) success = false;
            CloseHandle(handle);
        }
        return success;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(IntPtr threadHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr threadHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
