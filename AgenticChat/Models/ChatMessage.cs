namespace AgenticChat.Models;

/// <summary>
/// A single turn in a chat session (user or assistant).
/// </summary>
public class ChatMessage
{
    /// <summary>"user" or "assistant"</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Text content of the message.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the message was recorded.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
