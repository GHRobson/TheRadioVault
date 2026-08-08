using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Services;

public sealed class ApplicationShutdownCoordinator
{
    private int _started;

    public bool HasStarted => Volatile.Read(ref _started) != 0;

    public bool TryBegin() => Interlocked.Exchange(ref _started, 1) == 0;

    public ApplicationShutdownReport Execute(
        ApplicationShutdownContext context,
        IEnumerable<ApplicationShutdownStep> steps,
        Action<string>? stepStarting = null,
        Action<string>? stepCompleted = null,
        Action<string, Exception>? stepFailed = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(steps);

        if (!HasStarted)
            throw new InvalidOperationException("The shutdown sequence must be started before it is executed.");

        var results = new List<ApplicationShutdownStepResult>();
        foreach (var step in steps)
        {
            if (step.ShouldRun is not null && !step.ShouldRun(context))
            {
                results.Add(new ApplicationShutdownStepResult(step.Name, Succeeded: true, Skipped: true));
                continue;
            }

            try
            {
                stepStarting?.Invoke(step.Name);
                step.Execute();
                stepCompleted?.Invoke(step.Name);
                results.Add(new ApplicationShutdownStepResult(step.Name, Succeeded: true, Skipped: false));
            }
            catch (Exception ex)
            {
                stepFailed?.Invoke(step.Name, ex);
                results.Add(new ApplicationShutdownStepResult(step.Name, Succeeded: false, Skipped: false, ex.Message));
            }
        }

        return new ApplicationShutdownReport(results);
    }
}
