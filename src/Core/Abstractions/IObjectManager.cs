using TalosForge.Core.Models;

namespace TalosForge.Core.Abstractions;

public interface IObjectManager
{
    WorldSnapshot GetSnapshot(long tickId);
}
