namespace TheRadioVault.Application.Models;

public sealed record ApplicationShutdownContext(
    ApplicationSessionMode Mode,
    bool IsWindowTransition,
    bool IsDatabaseReset);

public sealed record ApplicationShutdownStep(
    string Name,
    Action Execute,
    Func<ApplicationShutdownContext, bool>? ShouldRun = null);

public sealed record ApplicationShutdownStepResult(
    string Name,
    bool Succeeded,
    bool Skipped,
    string? ErrorMessage = null);

public sealed record ApplicationShutdownReport(
    IReadOnlyList<ApplicationShutdownStepResult> Steps)
{
    public bool Succeeded => Steps.All(step => step.Succeeded || step.Skipped);
    public int FailedStepCount => Steps.Count(step => !step.Succeeded && !step.Skipped);
}
