using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticChat.Models;

namespace AgenticChat.Services;

/// <summary>
/// Stateless web-facing agentic chat service.
/// Each call loads the session, calls the LLM (with tool-call loop), then
/// persists the updated session back to disk.
/// </summary>
public class AgentChatService
{
    // ── Constants ──────────────────────────────────────────────────────────────
    private const int MaxShortTermMessages = 20;

    // ── Dependencies ───────────────────────────────────────────────────────────
    private readonly SessionService _sessionService;
    private readonly string _memoriesDir;

    // Shared HttpClient – reused for all requests to avoid socket exhaustion.
    private static readonly HttpClient s_http = new();
    private readonly string _apiUrl;
    private readonly string _model;

    // ── Tool definitions ───────────────────────────────────────────────────────
    private static readonly JsonArray s_tools = JsonNode.Parse("""
        [
          {
            "type": "function",
            "function": {
              "name": "add_memory",
              "description": "Add or update a long-term memory entry so it is available in future sessions.",
              "parameters": {
                "type": "object",
                "properties": {
                  "key":     { "type": "string", "description": "Short identifier for the memory." },
                  "content": { "type": "string", "description": "The information to store." }
                },
                "required": ["key", "content"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "search_memory",
              "description": "Search stored long-term memories for a given query.",
              "parameters": {
                "type": "object",
                "properties": {
                  "query": { "type": "string", "description": "Keyword or phrase to search for." }
                },
                "required": ["query"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "get_all_memories",
              "description": "Retrieve every stored long-term memory entry.",
              "parameters": { "type": "object", "properties": {}, "additionalProperties": false }
            }
          }
        ]
        """)!.AsArray();

    // ── Construction ───────────────────────────────────────────────────────────

    public AgentChatService(SessionService sessionService, string memoriesDir)
    {
        _sessionService = sessionService;
        _memoriesDir = memoriesDir;
        Directory.CreateDirectory(memoriesDir);
        (_apiUrl, _model) = ConfigureHttpClient();
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new session for the user, fires a greeting (init context-load),
    /// saves the greeting as the first assistant message, and returns the session.
    /// </summary>
    public async Task<ChatSession> CreateSessionAsync(string userGuid)
    {
        var session = _sessionService.CreateSession(userGuid);
        var memoryService = MemoryServiceFor(userGuid);

        var initMessages = new List<JsonObject>
        {
            SystemMessage(BuildSystemPrompt(memoryService)),
            UserMessage(
                "Before we begin, call get_all_memories to review everything you know " +
                "about me, then give a short, friendly greeting that shows you remember " +
                "relevant context. If no memories exist, just say hello."),
        };

        var greeting = await RunAgentLoopAsync(initMessages, memoryService);

        if (!string.IsNullOrWhiteSpace(greeting))
        {
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = greeting,
                Timestamp = DateTime.UtcNow,
            });
            session.UpdatedAt = DateTime.UtcNow;
            _sessionService.SaveSession(session);
        }

        return session;
    }

    /// <summary>
    /// Appends the user message to the session, runs the agent loop, appends the
    /// assistant reply, persists the session, and returns the reply text plus the
    /// (possibly updated) session title.
    /// </summary>
    public async Task<(string Reply, string SessionTitle)> SendMessageAsync(
        string userGuid, string sessionId, string userMessage)
    {
        var session = _sessionService.GetSession(userGuid, sessionId)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");

        var memoryService = MemoryServiceFor(userGuid);

        // Build API-format message list from short-term history
        var messages = new List<JsonObject> { SystemMessage(BuildSystemPrompt(memoryService)) };
        foreach (var m in session.Messages.TakeLast(MaxShortTermMessages))
        {
            messages.Add(m.Role == "user"
                ? UserMessage(m.Content)
                : AssistantMessage(m.Content));
        }
        messages.Add(UserMessage(userMessage));

        // Run the agent loop (tool calls + final response)
        var reply = await RunAgentLoopAsync(messages, memoryService);

        // Persist new turns
        session.Messages.Add(new ChatMessage { Role = "user",      Content = userMessage, Timestamp = DateTime.UtcNow });
        session.Messages.Add(new ChatMessage { Role = "assistant",  Content = reply,       Timestamp = DateTime.UtcNow });

        // Auto-title on first user message
        if (session.Messages.Count(m => m.Role == "user") == 1)
            session.Title = userMessage.Length > 60
                ? string.Concat(userMessage.AsSpan(0, 57), "...")
                : userMessage;

        session.UpdatedAt = DateTime.UtcNow;
        _sessionService.SaveSession(session);

        return (reply, session.Title);
    }

    // ── System prompt ──────────────────────────────────────────────────────────

    private static string BuildSystemPrompt(MemoryService memoryService) => $"""
        You are a helpful, proactive AI assistant with persistent long-term memory.

        ## Long-Term Memory (injected from previous sessions)
        {memoryService.FormatMemoriesForContext()}

        ## Guidelines
        - Use stored memories to personalise every response.
        - When the user shares something worth remembering (name, preferences, projects,
          decisions), call `add_memory` to persist it for future sessions.
        - When you need specific recalled information, use `search_memory` to look it up.
        - Be conversational, concise and helpful.
        """;

    // ── Tool execution ─────────────────────────────────────────────────────────

    private string ExecuteTool(string toolName, string toolArgumentsJson, MemoryService memoryService)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolArgumentsJson);
            var root = doc.RootElement;

            return toolName switch
            {
                "add_memory"      => memoryService.AddOrUpdateMemory(
                                         GetStringArg(root, "key"),
                                         GetStringArg(root, "content")),
                "search_memory"   => FormatSearchResults(
                                         memoryService.SearchMemories(GetStringArg(root, "query"))),
                "get_all_memories" => memoryService.FormatMemoriesForContext(),
                _                 => $"Unknown tool: '{toolName}'.",
            };
        }
        catch (Exception ex)
        {
            return $"Tool error: {ex.Message}";
        }
    }

    private static string GetStringArg(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) ? el.GetString() ?? string.Empty : string.Empty;

    private static string FormatSearchResults(IEnumerable<MemoryEntry> results)
    {
        var list = results.ToList();
        return list.Count == 0
            ? "No matching memories found."
            : string.Join("\n", list.Select(m => $"[{m.Key}]: {m.Content}"));
    }

    // ── Raw API call ───────────────────────────────────────────────────────────

    private async Task<JsonObject> PostChatCompletionAsync(List<JsonObject> messages)
    {
        var requestBody = new JsonObject
        {
            ["model"]       = _model,
            ["messages"]    = new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
            ["tools"]       = s_tools.DeepClone(),
            ["tool_choice"] = "auto",
        };

        var body = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await s_http.PostAsync(_apiUrl, body);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        return (await JsonNode.ParseAsync(stream))!.AsObject();
    }

    // ── Agentic tool-call loop ─────────────────────────────────────────────────

    private async Task<string> RunAgentLoopAsync(
        List<JsonObject> messages, MemoryService memoryService)
    {
        while (true)
        {
            var response     = await PostChatCompletionAsync(messages);
            var choice       = response["choices"]![0]!;
            var finishReason = choice["finish_reason"]?.GetValue<string>();
            var message      = choice["message"]!.AsObject();

            if (finishReason == "tool_calls")
            {
                messages.Add(message.DeepClone().AsObject());

                foreach (var toolCallNode in message["tool_calls"]!.AsArray())
                {
                    var toolCall     = toolCallNode!.AsObject();
                    var id           = toolCall["id"]!.GetValue<string>();
                    var functionName = toolCall["function"]!["name"]!.GetValue<string>();
                    var functionArgs = toolCall["function"]!["arguments"]!.GetValue<string>();

                    var result = ExecuteTool(functionName, functionArgs, memoryService);
                    messages.Add(new JsonObject
                    {
                        ["role"]         = "tool",
                        ["tool_call_id"] = id,
                        ["content"]      = result,
                    });
                }
            }
            else
            {
                return message["content"]?.GetValue<string>() ?? string.Empty;
            }
        }
    }

    // ── Message helpers ────────────────────────────────────────────────────────

    private static JsonObject SystemMessage(string content) =>
        new() { ["role"] = "system",    ["content"] = content };

    private static JsonObject UserMessage(string content) =>
        new() { ["role"] = "user",      ["content"] = content };

    private static JsonObject AssistantMessage(string content) =>
        new() { ["role"] = "assistant", ["content"] = content };

    // ── HTTP client configuration ──────────────────────────────────────────────

    private static (string apiUrl, string model) ConfigureHttpClient()
    {
        var azureEndpoint   = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var azureKey        = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var azureDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";

        if (!string.IsNullOrWhiteSpace(azureEndpoint) && !string.IsNullOrWhiteSpace(azureKey))
        {
            s_http.DefaultRequestHeaders.Remove("api-key");
            s_http.DefaultRequestHeaders.Add("api-key", azureKey);
            var url = $"{azureEndpoint.TrimEnd('/')}/openai/deployments/{azureDeployment}/chat/completions?api-version=2024-10-21";
            return (url, azureDeployment);
        }

        var openAiKey     = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var openAiModel   = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";
        var openAiBaseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");

        if (!string.IsNullOrWhiteSpace(openAiBaseUrl))
        {
            var apiKey = !string.IsNullOrWhiteSpace(openAiKey) ? openAiKey : "none";
            s_http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
            return ($"{openAiBaseUrl.TrimEnd('/')}/chat/completions", openAiModel);
        }

        if (!string.IsNullOrWhiteSpace(openAiKey))
        {
            s_http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", openAiKey);
            return ("https://api.openai.com/v1/chat/completions", openAiModel);
        }

        throw new InvalidOperationException(
            "No LLM credentials found. Set one of:\n" +
            "  • AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY  (Azure OpenAI)\n" +
            "  • OPENAI_BASE_URL [+ OPENAI_API_KEY]            (any OpenAI-compatible endpoint)\n" +
            "  • OPENAI_API_KEY                                 (OpenAI)");
    }

    // ── Memory service factory ─────────────────────────────────────────────────

    private MemoryService MemoryServiceFor(string userGuid) =>
        new(Path.Combine(_memoriesDir, $"{userGuid}_memory.json"));
}
