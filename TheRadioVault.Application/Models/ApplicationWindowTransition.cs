namespace TheRadioVault.Application.Models;

public sealed record ApplicationWindowTransition(
    Guid TransitionId,
    ApplicationSessionMode SourceMode,
    ApplicationSessionMode TargetMode,
    DateTimeOffset StartedAtUtc)
{
    public bool ChangesMode => SourceMode != TargetMode;
}
