using TalosForge.Core.Abstractions;
using TalosForge.Core.Caching;
using TalosForge.Core.Configuration;
using Xunit;

namespace TalosForge.Tests.Caching;

public sealed class MemoryCacheServiceTests
{
    [Fact]
    public void ShortTtl_Expires_As_Expected()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = new MemoryCacheService(new BotOptions(), clock);

        cache.Set("short", 5, CachePolicy.ShortLived);
        Assert.True(cache.TryGet<int>("short", out var value));
        Assert.Equal(5, value);

        clock.Advance(TimeSpan.FromMilliseconds(101));
        Assert.False(cache.TryGet<int>("short", out _));
    }

    [Fact]
    public void LongTtl_Persists_Within_Window()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = new MemoryCacheService(new BotOptions(), clock);

        cache.Set("long", "value", CachePolicy.LongLived);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(cache.TryGet<string>("long", out var value));
        Assert.Equal("value", value);
    }

    [Fact]
    public void NonePolicy_Does_Not_Store()
    {
        var cache = new MemoryCacheService(new BotOptions());
        cache.Set("position", 99, CachePolicy.None);

        Assert.False(cache.TryGet<int>("position", out _));
    }
}
