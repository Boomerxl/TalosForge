using TalosForge.Core.Models;

namespace TalosForge.Core.Abstractions;

/// <summary>
/// Optional transport telemetry surface for unlocker health reporting.
/// </summary>
public interface IUnlockerTelemetrySource
{
    UnlockerClientMetrics GetMetricsSnapshot();
}
