namespace TalosForge.Core.Abstractions;

public enum CachePolicy
{
    None = 0,
    ShortLived = 1,
    LongLived = 2,
}

public interface ICacheService
{
    void Set<T>(string key, T value, CachePolicy policy);
    bool TryGet<T>(string key, out T? value);
    void Invalidate(string key);
    void Clear();
}
