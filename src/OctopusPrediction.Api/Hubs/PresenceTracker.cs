namespace OctopusPrediction.Api.Hubs;

// Who has the app open right now, counted per connection so a second tab closing doesn't
// mark someone offline. In memory, so it assumes a single API instance — scaling out would
// need a SignalR backplane (e.g. Redis) and a shared store for this too.
//
// Separately, whether each connection's app is in front (ChatHub.SetActive): a phone keeps its
// connection for a while with the app in the background, which still counts as online but not
// as active. Chat messages are pushed to anyone not active (ChatPushNotifier).
public class PresenceTracker
{
    private readonly Dictionary<Guid, int> _connections = [];
    private readonly Dictionary<string, (Guid UserId, bool Active)> _byConnection = [];
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

    // A new connection counts as active: the app has just been opened, so it's in front. An app
    // too old to report otherwise stays active while connected, as everyone was before.
    public void Track(string connectionId, Guid userId)
    {
        lock (_lock) _byConnection[connectionId] = (userId, true);
    }

    public void SetActive(string connectionId, bool active)
    {
        lock (_lock)
        {
            if (_byConnection.TryGetValue(connectionId, out var c)) _byConnection[connectionId] = (c.UserId, active);
        }
    }

    public void Forget(string connectionId)
    {
        lock (_lock) _byConnection.Remove(connectionId);
    }

    /// <summary>Whether the user has the app in front on any device.</summary>
    public bool IsActive(Guid userId)
    {
        lock (_lock) return _byConnection.Values.Any(c => c.UserId == userId && c.Active);
    }
}
