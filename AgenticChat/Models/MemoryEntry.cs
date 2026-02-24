namespace AgenticChat.Models;

/// <summary>
/// Represents a single long-term memory entry persisted across sessions.
/// </summary>
public class MemoryEntry
{
    /// <summary>Short identifier for the memory (e.g. "user_name", "project_context").</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The content to remember.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the memory was first created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp of the most recent update.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
