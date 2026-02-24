using System.Text.Json;
using AgenticChat.Models;

namespace AgenticChat.Services;

/// <summary>
/// Manages per-user chat sessions persisted as JSON files on disk.
/// Layout:  {sessionsDir}/{userGuid}/{sessionId}.json
/// </summary>
public class SessionService
{
    private readonly string _sessionsDir;

    private static readonly JsonSerializerOptions s_jsonOptions =
        new() { WriteIndented = true };

    public SessionService(string sessionsDir)
    {
        _sessionsDir = sessionsDir;
        Directory.CreateDirectory(sessionsDir);
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all sessions for the given user, newest-first (by UpdatedAt).
    /// </summary>
    public List<ChatSession> GetUserSessions(string userGuid)
    {
        var userDir = UserDir(userGuid);
        if (!Directory.Exists(userDir))
            return [];

        var sessions = new List<ChatSession>();
        foreach (var file in Directory.GetFiles(userDir, "*.json"))
        {
            var session = TryLoad(file);
            if (session is not null)
                sessions.Add(session);
        }

        return [.. sessions.OrderByDescending(s => s.UpdatedAt)];
    }

    /// <summary>Returns the session, or <c>null</c> if not found.</summary>
    public ChatSession? GetSession(string userGuid, string sessionId)
    {
        var path = SessionPath(userGuid, sessionId);
        return File.Exists(path) ? TryLoad(path) : null;
    }

    /// <summary>Creates a new empty session for the user and persists it.</summary>
    public ChatSession CreateSession(string userGuid)
    {
        Directory.CreateDirectory(UserDir(userGuid));
        var session = new ChatSession { UserGuid = userGuid };
        SaveSession(session);
        return session;
    }

    /// <summary>Persists an existing session to disk.</summary>
    public void SaveSession(ChatSession session)
    {
        Directory.CreateDirectory(UserDir(session.UserGuid));
        File.WriteAllText(
            SessionPath(session.UserGuid, session.Id),
            JsonSerializer.Serialize(session, s_jsonOptions));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string UserDir(string userGuid) =>
        Path.Combine(_sessionsDir, userGuid);

    private string SessionPath(string userGuid, string sessionId) =>
        Path.Combine(UserDir(userGuid), $"{sessionId}.json");

    private static ChatSession? TryLoad(string filePath)
    {
        try
        {
            return JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(filePath));
        }
        catch
        {
            return null;
        }
    }
}
