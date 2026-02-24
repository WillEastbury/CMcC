namespace AgenticChat.Models;

/// <summary>
/// A persistent chat session owned by a user (identified by their anonymous GUID).
/// </summary>
public class ChatSession
{
    /// <summary>Unique session identifier (GUID string).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The anonymous user GUID that owns this session.</summary>
    public string UserGuid { get; set; } = string.Empty;

    /// <summary>Auto-generated title derived from the first user message.</summary>
    public string Title { get; set; } = "New Session";

    /// <summary>UTC timestamp when the session was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp of the most recent update.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Ordered list of conversation turns.</summary>
    public List<ChatMessage> Messages { get; set; } = [];
}
