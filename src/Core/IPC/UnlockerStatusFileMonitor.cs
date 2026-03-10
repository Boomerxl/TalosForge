using System.Text.Json;
using TalosForge.Core.Models;

namespace TalosForge.Core.IPC;

/// <summary>
/// Reads UnlockerHost heartbeat data from a shared status file with lightweight caching.
/// </summary>
public sealed class UnlockerStatusFileMonitor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _statusFilePath;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _readInterval;
    private readonly object _syncRoot = new();

    private DateTimeOffset _nextReadUtc = DateTimeOffset.MinValue;
    private UnlockerHostStatusFile? _lastStatus;

    public UnlockerStatusFileMonitor(string statusFilePath, TimeSpan staleAfter, TimeSpan readInterval)
    {
        _statusFilePath = statusFilePath;
        _staleAfter = staleAfter;
        _readInterval = readInterval;
    }

    public UnlockerHostStatusFile? GetStatus()
    {
        lock (_syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _nextReadUtc)
            {
                return _lastStatus;
            }

            _nextReadUtc = now.Add(_readInterval);
            if (!File.Exists(_statusFilePath))
            {
                _lastStatus = null;
                return null;
            }

            try
            {
                var json = File.ReadAllText(_statusFilePath);
                _lastStatus = JsonSerializer.Deserialize<UnlockerHostStatusFile>(json, JsonOptions);
            }
            catch
            {
                _lastStatus = null;
            }

            return _lastStatus;
        }
    }

    public bool IsFresh(UnlockerHostStatusFile? status)
    {
        if (status == null)
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - status.TimestampUtc;
        return age <= _staleAfter;
    }
}
