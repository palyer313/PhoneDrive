namespace PhoneDrive.Core.Discovery;

/// <summary>Uses confirmed transport failures and bounded retry windows instead of discovery flaps.</summary>
public sealed class ConnectionStabilityPolicy
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Since, int Misses)> _losses = new();
    private readonly Dictionary<string, (int Failures, DateTimeOffset RetryAt)> _mounts = new();
    private readonly HashSet<string> _mounting = new();

    public bool ConfirmOffline(string id, bool reachable, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (reachable) { _losses.Remove(id); return false; }
            var loss = _losses.TryGetValue(id, out var old) ? (old.Since, old.Misses + 1) : (now, 1);
            _losses[id] = loss;
            return loss.Item2 >= 3 && now - loss.Item1 >= TimeSpan.FromSeconds(20);
        }
    }

    public bool TryBeginMount(string id, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_mounting.Contains(id) || (_mounts.TryGetValue(id, out var retry) && retry.RetryAt > now)) return false;
            _mounting.Add(id); return true;
        }
    }

    public void EndMount(string id, bool success, DateTimeOffset now)
    {
        lock (_gate)
        {
            _mounting.Remove(id);
            if (success) { _mounts.Remove(id); return; }
            var failures = _mounts.TryGetValue(id, out var old) ? old.Failures + 1 : 1;
            _mounts[id] = (failures, now.AddSeconds(Math.Min(60, 5 * (1 << Math.Min(failures - 1, 4)))));
        }
    }
}

public sealed class NotificationGate
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _sent = new();
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    public bool TryPublish(string key, DateTimeOffset now, bool critical = false)
    {
        lock (_gate)
        {
            if (_sent.TryGetValue(key, out var sent) && now - sent < TimeSpan.FromMinutes(5)) return false;
            if (!critical && now - _last < TimeSpan.FromSeconds(10)) return false;
            _sent[key] = now; _last = now;
            foreach (var stale in _sent.Where(p => now - p.Value >= TimeSpan.FromMinutes(5)).Select(p => p.Key).ToArray()) _sent.Remove(stale);
            return true;
        }
    }
}
