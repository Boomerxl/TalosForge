using TalosForge.AdapterBridge.Models;

namespace TalosForge.AdapterBridge.Execution;

public interface IBridgeCommandExecutor
{
    ValueTask<AdapterPipeResponse> ExecuteAsync(AdapterPipeRequest request, CancellationToken cancellationToken);
}
