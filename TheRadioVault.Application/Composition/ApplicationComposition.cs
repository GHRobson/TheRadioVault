namespace TheRadioVault.Application.Composition;

public enum ApplicationServiceLifetime
{
    Singleton,
    Transient
}

public sealed record ApplicationServiceRegistration(
    string ServiceType,
    ApplicationServiceLifetime Lifetime,
    bool InstanceCreated);

public sealed record ApplicationCompositionReport(
    IReadOnlyList<ApplicationServiceRegistration> Registrations,
    IReadOnlyList<string> MissingRequiredServices,
    bool IsFrozen)
{
    public bool IsValid => MissingRequiredServices.Count == 0;

    public string ToDiagnosticText()
    {
        var singletonCount = Registrations.Count(x => x.Lifetime == ApplicationServiceLifetime.Singleton);
        var transientCount = Registrations.Count(x => x.Lifetime == ApplicationServiceLifetime.Transient);
        var createdCount = Registrations.Count(x => x.InstanceCreated);
        var missing = MissingRequiredServices.Count == 0
            ? "none"
            : string.Join(", ", MissingRequiredServices);

        return $"services={Registrations.Count}; singletons={singletonCount}; transients={transientCount}; created={createdCount}; frozen={IsFrozen}; missing={missing}";
    }
}
