namespace OctopusPrediction.Api.Hubs;

// Who has the app open right now, counted per connection so a second tab closing doesn't
// mark someone offline. In memory, so it assumes a single API instance — scaling out would
// need a SignalR backplane (e.g. Redis) and a shared store for this too.
public class PresenceTracker
{
    private readonly Dictionary<Guid, int> _connections = new();
    private readonly object _lock = new();

    // True when this is the user's first open connection (they just came online).
    public bool Connect(Guid userId)
    {
        lock (_lock)
        {
            _connections[userId] = _connections.GetValueOrDefault(userId) + 1;
            return _connections[userId] == 1;
        }
    }

    // True when that was the user's last open connection (they just went offline).
    public bool Disconnect(Guid userId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(userId, out var count)) return false;
            if (count > 1)
            {
                _connections[userId] = count - 1;
                return false;
            }
            _connections.Remove(userId);
            return true;
        }
    }

    public bool IsOnline(Guid userId)
    {
        lock (_lock) return _connections.ContainsKey(userId);
    }

    public HashSet<Guid> Online()
    {
        lock (_lock) return [.. _connections.Keys];
    }
}
