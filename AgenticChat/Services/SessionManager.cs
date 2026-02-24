using AgenticChat.Models;

namespace AgenticChat.Services;

/// <summary>
/// Manages a collection of named <see cref="ChatSession"/> objects for the current
/// application run.  Sessions are in-memory only; long-term memories (handled by
/// <see cref="MemoryService"/>) continue to persist across application restarts.
/// </summary>
public sealed class SessionManager
{
    private readonly List<ChatSession> _sessions = [];

    /// <summary>All sessions, in creation order.</summary>
    public IReadOnlyList<ChatSession> Sessions => _sessions;

    /// <summary>The currently active session.</summary>
    public ChatSession Active { get; private set; }

    public SessionManager()
    {
        // Always start with a default session
        Active = CreateSession("default");
    }

    /// <summary>
    /// Creates a new session with the given <paramref name="name"/>, adds it to
    /// the managed list, and makes it the active session.
    /// </summary>
    public ChatSession CreateSession(string name)
    {
        var session = new ChatSession(name.Trim());
        _sessions.Add(session);
        Active = session;
        return session;
    }

    /// <summary>
    /// Switches the active session by 1-based index or (case-insensitive) name.
    /// Returns the switched-to session, or <c>null</c> if not found.
    /// </summary>
    public ChatSession? SwitchTo(string nameOrIndex)
    {
        ChatSession? found = null;

        if (int.TryParse(nameOrIndex.Trim(), out var idx) &&
            idx >= 1 && idx <= _sessions.Count)
        {
            found = _sessions[idx - 1];
        }
        else
        {
            found = _sessions.FirstOrDefault(
                s => s.Name.Equals(nameOrIndex.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (found is not null)
        {
            Active = found;
            Active.LastActiveAt = DateTime.UtcNow;
        }

        return found;
    }
}
