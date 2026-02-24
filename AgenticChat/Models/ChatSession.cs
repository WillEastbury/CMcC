using OpenAI.Chat;

namespace AgenticChat.Models;

/// <summary>
/// Represents a single named chat session with its own isolated message history.
/// Multiple sessions allow parallel conversations that share the same long-term
/// memory store but maintain independent short-term histories.
/// </summary>
public sealed class ChatSession
{
    private const int IdLength = 8;

    /// <summary>Short random identifier used for display purposes.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N")[..IdLength];

    /// <summary>Human-readable session name.</summary>
    public string Name { get; set; }

    /// <summary>UTC time the session was created.</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>UTC time of the last user/assistant message in this session.</summary>
    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;

    /// <summary>Number of completed user→assistant turns in this session.</summary>
    public int TurnCount { get; set; }

    /// <summary>
    /// Short-term message history (user + assistant messages, sliding window).
    /// </summary>
    public List<ChatMessage> History { get; } = [];

    public ChatSession(string name) => Name = name;
}
