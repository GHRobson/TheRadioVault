using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Services;

public sealed class ApplicationWindowTransitionCoordinator
{
    private int _transitionStarted;

    public bool IsTransitionInProgress => Volatile.Read(ref _transitionStarted) != 0;

    public bool TryBegin(
        ApplicationSessionMode sourceMode,
        ApplicationSessionMode targetMode,
        out ApplicationWindowTransition transition)
    {
        if (Interlocked.Exchange(ref _transitionStarted, 1) != 0)
        {
            transition = null!;
            return false;
        }

        transition = new ApplicationWindowTransition(
            Guid.NewGuid(),
            sourceMode,
            targetMode,
            DateTimeOffset.UtcNow);
        return true;
    }
}
