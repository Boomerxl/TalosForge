using System.Runtime.Loader;

namespace TalosForge.Core.Plugins;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string CoreAssemblyName =
        typeof(Program).Assembly.GetName().Name ?? "TalosForge.Core";

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string mainAssemblyPath)
        : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName assemblyName)
    {
        // Share core contract assembly from the default context to avoid type-identity mismatch.
        if (string.Equals(assemblyName.Name, CoreAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path != null)
        {
            return LoadFromAssemblyPath(path);
        }

        return null;
    }
}
