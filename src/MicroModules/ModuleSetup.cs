using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MicroModules;

/// <summary>Optional application-owned setup for a locally hosted module.</summary>
public interface IMicroModule
{
    static abstract void Configure(ModuleSetupContext module);
}

/// <summary>Registers local resources and declares configurable module dependencies.</summary>
public sealed class ModuleSetupContext
{
    private readonly Action<Type, Type> addDependency;
    private bool active = true;

    public IServiceCollection Services { get; }
    public IConfigurationSection Configuration { get; }

    internal ModuleSetupContext(IServiceCollection services, IConfigurationSection configuration,
        Action<Type, Type> addDependency)
        => (Services, Configuration, this.addDependency) = (services, configuration, addDependency);

    public void AddDependency<TContract, TImplementation>() where TContract : class
        where TImplementation : class, TContract
    {
        if (!active) throw new InvalidOperationException("Declare dependencies only during module setup.");
        addDependency(typeof(TContract), typeof(TImplementation));
    }

    internal void Complete() => active = false;
}
