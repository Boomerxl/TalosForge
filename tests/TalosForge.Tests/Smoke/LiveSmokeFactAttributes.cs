using Xunit;

namespace TalosForge.Tests.Smoke;

public sealed class LiveWowFactAttribute : FactAttribute
{
    public LiveWowFactAttribute()
    {
        Skip = LiveSmokePreconditions.GetWowSkipReason();
    }
}

public sealed class LiveInjectedFactAttribute : FactAttribute
{
    public LiveInjectedFactAttribute()
    {
        Skip = LiveSmokePreconditions.GetInjectedSkipReason();
    }
}
