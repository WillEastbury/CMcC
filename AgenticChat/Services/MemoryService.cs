using System.Text.Json;
using AgenticChat.Models;

namespace AgenticChat.Services;

/// <summary>
/// Manages long-term memory entries, persisted as a JSON file on disk.
/// Provides add/update, search, and formatting capabilities used both by
/// the agent tools and the system-prompt injection pipeline.
/// </summary>
public class MemoryService
{
    private readonly string _filePath;
    private List<MemoryEntry> _memories;

    private static readonly JsonSerializerOptions s_jsonOptions =
        new() { WriteIndented = true };

    public MemoryService(string filePath)
    {
        _filePath = filePath;
        _memories = LoadFromDisk();
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    private List<MemoryEntry> LoadFromDisk()
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<MemoryEntry>>(json) ?? [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MemoryService] Failed to load '{_filePath}': {ex.Message}. Starting with empty memory.");
            return [];
        }
    }

    private void SaveToDisk()
    {
        var json = JsonSerializer.Serialize(_memories, s_jsonOptions);
        File.WriteAllText(_filePath, json);
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a new memory or updates an existing one with the same key (case-insensitive).
    /// Returns a human-readable confirmation message.
    /// </summary>
    public string AddOrUpdateMemory(string key, string content)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "Error: memory key must not be empty.";

        if (string.IsNullOrWhiteSpace(content))
            return "Error: memory content must not be empty.";

        var existing = _memories.FirstOrDefault(
            m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Content = content;
            existing.UpdatedAt = DateTime.UtcNow;
            SaveToDisk();
            return $"Memory '{key}' updated.";
        }

        _memories.Add(new MemoryEntry
        {
            Key = key,
            Content = content,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        SaveToDisk();
        return $"Memory '{key}' added.";
    }

    /// <summary>Returns memories whose key or content contains <paramref name="query"/>.</summary>
    public IEnumerable<MemoryEntry> SearchMemories(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _memories;

        return _memories.Where(m =>
            m.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            m.Content.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns every stored memory entry.</summary>
    public IEnumerable<MemoryEntry> GetAllMemories() => _memories;

    /// <summary>
    /// Formats all memories as a compact bullet list suitable for injection
    /// into a system prompt.
    /// </summary>
    public string FormatMemoriesForContext()
    {
        if (_memories.Count == 0)
            return "(No long-term memories stored yet.)";

        return string.Join("\n", _memories.Select(m => $"- [{m.Key}]: {m.Content}"));
    }
}
