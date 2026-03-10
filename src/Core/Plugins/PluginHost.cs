using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Models;

namespace TalosForge.Core.Plugins;

/// <summary>
/// In-process plugin host with isolated load contexts and sandboxed command queue API.
/// </summary>
public sealed class PluginHost : IDisposable
{
    private readonly string _pluginDirectory;
    private readonly ILogger<PluginHost> _logger;
    private readonly List<LoadedPlugin> _plugins = new();
    private readonly Version _coreVersion;
    private bool _disposed;

    public PluginHost(string pluginDirectory, ILogger<PluginHost> logger)
    {
        _pluginDirectory = pluginDirectory;
        _logger = logger;
        _coreVersion = typeof(PluginHost).Assembly.GetName().Version ?? new Version(1, 0, 0);
    }

    public IReadOnlyList<string> LoadedPluginNames => _plugins.Select(p => p.Instance.Name).ToArray();

    public void LoadPlugins()
    {
        ThrowIfDisposed();

        if (!Directory.Exists(_pluginDirectory))
        {
            _logger.LogWarning("Plugin directory does not exist: {Directory}", _pluginDirectory);
            return;
        }

        var manifests = Directory.GetFiles(_pluginDirectory, "*.plugin.json", SearchOption.AllDirectories);
        foreach (var manifestPath in manifests)
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath));
            if (manifest == null)
            {
                _logger.LogWarning("Skipping invalid plugin manifest {ManifestPath}", manifestPath);
                continue;
            }

            if (!Version.TryParse(manifest.MinimumCoreVersion, out var minimumVersion))
            {
                _logger.LogWarning("Skipping plugin {PluginName}: invalid minimum version", manifest.Name);
                continue;
            }

            if (_coreVersion < minimumVersion)
            {
                _logger.LogWarning(
                    "Skipping plugin {PluginName}: requires core {Required}, current {Current}",
                    manifest.Name,
                    minimumVersion,
                    _coreVersion);
                continue;
            }

            var pluginAssemblyPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest.Assembly);
            if (!File.Exists(pluginAssemblyPath))
            {
                _logger.LogWarning("Skipping plugin {PluginName}: assembly not found at {Path}", manifest.Name, pluginAssemblyPath);
                continue;
            }

            var loadContext = new PluginLoadContext(pluginAssemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(pluginAssemblyPath);
            var pluginType = assembly.GetType(manifest.Type, throwOnError: false);

            if (pluginType == null || !typeof(IPlugin).IsAssignableFrom(pluginType))
            {
                _logger.LogWarning("Skipping plugin {PluginName}: type {TypeName} is invalid", manifest.Name, manifest.Type);
                loadContext.Unload();
                continue;
            }

            var instance = (IPlugin?)Activator.CreateInstance(pluginType);
            if (instance == null)
            {
                _logger.LogWarning("Skipping plugin {PluginName}: failed to construct type", manifest.Name);
                loadContext.Unload();
                continue;
            }

            var context = new PluginRuntimeContext();
            instance.Initialize(context);

            _plugins.Add(new LoadedPlugin(instance, context, loadContext));
            _logger.LogInformation("Loaded plugin {PluginName} v{Version}", instance.Name, instance.Version);
        }
    }

    public async Task<int> TickAsync(
        WorldSnapshot snapshot,
        IReadOnlyList<BotEvent> events,
        IUnlockerClient unlockerClient,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var commandsSent = 0;

        foreach (var plugin in _plugins)
        {
            plugin.Context.SetTickContext(snapshot, events);
            await plugin.Instance.TickAsync(snapshot, events, cancellationToken).ConfigureAwait(false);

            while (plugin.Context.TryDequeue(out var command))
            {
                await unlockerClient.SendAsync(command, cancellationToken).ConfigureAwait(false);
                commandsSent++;
            }
        }

        return commandsSent;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var plugin in _plugins)
        {
            try
            {
                plugin.Instance.Dispose();
            }
            finally
            {
                plugin.LoadContext.Unload();
            }
        }

        _plugins.Clear();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PluginHost));
        }
    }

    private sealed record LoadedPlugin(IPlugin Instance, PluginRuntimeContext Context, PluginLoadContext LoadContext);
}
