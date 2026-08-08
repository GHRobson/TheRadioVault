using System.Threading;

namespace TheRadioVault.Application.Composition;

/// <summary>
/// Small dependency-injection registry used by Radio Vault's composition root.
/// It intentionally avoids a dependency on a UI framework or an external DI package.
/// Registrations are frozen after startup so presentation code cannot silently mutate
/// the application's dependency graph while it is running.
/// </summary>
public sealed class ApplicationServiceRegistry : IDisposable
{
    private sealed class ServiceEntry
    {
        public ServiceEntry(
            Type serviceType,
            ApplicationServiceLifetime lifetime,
            Func<ApplicationServiceRegistry, object> factory,
            object? instance,
            bool instanceCreated,
            int registrationOrder)
        {
            ServiceType = serviceType;
            Lifetime = lifetime;
            Factory = factory;
            Instance = instance;
            InstanceCreated = instanceCreated;
            RegistrationOrder = registrationOrder;
        }

        public Type ServiceType { get; }
        public ApplicationServiceLifetime Lifetime { get; }
        public Func<ApplicationServiceRegistry, object> Factory { get; }
        public object CreationGate { get; } = new();
        public object? Instance { get; set; }
        public bool InstanceCreated { get; set; }
        public int RegistrationOrder { get; }
    }

    private static readonly AsyncLocal<Stack<Type>?> ResolutionPath = new();
    private readonly object _gate = new();
    private readonly Dictionary<Type, ServiceEntry> _entries = new();
    private int _registrationOrder;
    private bool _frozen;
    private bool _disposed;

    public bool IsFrozen
    {
        get
        {
            lock (_gate) return _frozen;
        }
    }

    public ApplicationServiceRegistry RegisterSingleton<TService>(TService instance)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        return Register(
            typeof(TService),
            ApplicationServiceLifetime.Singleton,
            _ => instance,
            instance,
            instanceCreated: true);
    }

    public ApplicationServiceRegistry RegisterSingleton<TService>(Func<TService> factory)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return RegisterSingleton(_ => factory());
    }

    public ApplicationServiceRegistry RegisterSingleton<TService>(Func<ApplicationServiceRegistry, TService> factory)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Register(
            typeof(TService),
            ApplicationServiceLifetime.Singleton,
            registry => factory(registry),
            instance: null,
            instanceCreated: false);
    }

    public ApplicationServiceRegistry RegisterFactory<TService>(Func<TService> factory)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return RegisterFactory(_ => factory());
    }

    public ApplicationServiceRegistry RegisterFactory<TService>(Func<ApplicationServiceRegistry, TService> factory)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Register(
            typeof(TService),
            ApplicationServiceLifetime.Transient,
            registry => factory(registry),
            instance: null,
            instanceCreated: false);
    }

    public TService GetRequiredService<TService>()
        where TService : class
        => (TService)GetRequiredService(typeof(TService));

    public bool TryGetService<TService>(out TService? service)
        where TService : class
    {
        ServiceEntry? entry;
        lock (_gate)
        {
            ThrowIfDisposed();
            _entries.TryGetValue(typeof(TService), out entry);
        }

        if (entry is null)
        {
            service = null;
            return false;
        }

        service = (TService)Resolve(entry);
        return true;
    }

    public ApplicationCompositionReport CreateCompositionReport(params Type[] requiredServices)
    {
        requiredServices ??= Array.Empty<Type>();
        lock (_gate)
        {
            ThrowIfDisposed();
            var registrations = _entries.Values
                .OrderBy(x => x.RegistrationOrder)
                .Select(x => new ApplicationServiceRegistration(
                    x.ServiceType.FullName ?? x.ServiceType.Name,
                    x.Lifetime,
                    x.InstanceCreated))
                .ToArray();
            var missing = requiredServices
                .Where(type => type is not null && !_entries.ContainsKey(type))
                .Select(type => type.FullName ?? type.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            return new ApplicationCompositionReport(registrations, missing, _frozen);
        }
    }

    public ApplicationServiceRegistry Freeze()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _frozen = true;
        }
        return this;
    }

    private ApplicationServiceRegistry Register(
        Type serviceType,
        ApplicationServiceLifetime lifetime,
        Func<ApplicationServiceRegistry, object> factory,
        object? instance,
        bool instanceCreated)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_frozen)
                throw new InvalidOperationException("Application service registrations are frozen.");
            if (_entries.ContainsKey(serviceType))
                throw new InvalidOperationException($"Application service {serviceType.FullName} is already registered.");

            _entries.Add(serviceType, new ServiceEntry(
                serviceType,
                lifetime,
                factory,
                instance,
                instanceCreated,
                _registrationOrder++));
        }
        return this;
    }

    private object GetRequiredService(Type serviceType)
    {
        ServiceEntry? entry;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(serviceType, out entry))
                throw new InvalidOperationException($"Application service {serviceType.FullName} is not registered.");
        }
        return Resolve(entry!);
    }

    private object Resolve(ServiceEntry entry)
    {
        if (entry.Lifetime == ApplicationServiceLifetime.Transient)
            return CreateInstance(entry);

        lock (entry.CreationGate)
        {
            if (entry.InstanceCreated)
                return entry.Instance!;

            var instance = CreateInstance(entry);
            entry.Instance = instance;
            entry.InstanceCreated = true;
            return instance;
        }
    }

    private object CreateInstance(ServiceEntry entry)
    {
        var path = ResolutionPath.Value ??= new Stack<Type>();
        if (path.Contains(entry.ServiceType))
        {
            var chain = string.Join(" -> ", path.Reverse().Append(entry.ServiceType).Select(x => x.Name));
            throw new InvalidOperationException($"Cyclic application service dependency detected: {chain}.");
        }

        path.Push(entry.ServiceType);
        try
        {
            return entry.Factory(this)
                   ?? throw new InvalidOperationException($"Application service factory for {entry.ServiceType.FullName} returned null.");
        }
        finally
        {
            path.Pop();
            if (path.Count == 0) ResolutionPath.Value = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ApplicationServiceRegistry));
    }

    public void Dispose()
    {
        ServiceEntry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _frozen = true;
            entries = _entries.Values
                .Where(x => x.Lifetime == ApplicationServiceLifetime.Singleton && x.InstanceCreated)
                .OrderByDescending(x => x.RegistrationOrder)
                .ToArray();
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            if (entry.Instance is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch { }
            }
        }
    }
}
