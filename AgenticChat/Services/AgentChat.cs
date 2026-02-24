using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticChat.Models;

namespace AgenticChat.Services;

/// <summary>
/// Agentic chat service that combines:
/// <list type="bullet">
///   <item>Short-term conversational history (sliding window)</item>
///   <item>Long-term memory tools that persist information across sessions</item>
///   <item>Memory injected into every system prompt (RAG-style)</item>
///   <item>A startup context-loading sequence that fires tool calls before the
///         first user message</item>
/// </list>
/// </summary>
public class AgentChat
{
    // ── Constants ──────────────────────────────────────────────────────────────
    private const int MaxShortTermMessages = 20;

    // ── Dependencies ───────────────────────────────────────────────────────────
    private readonly MemoryService _memoryService;

    // Shared HttpClient – reused for all requests to avoid socket exhaustion.
    private static readonly HttpClient s_http = new();
    private readonly string _apiUrl;
    private readonly string _model;

    // ── Short-term history (user + assistant pairs only) ───────────────────────
    private readonly List<JsonObject> _history = [];

    // ── Tool definitions ───────────────────────────────────────────────────────

    private static readonly JsonArray s_tools = JsonNode.Parse("""
        [
          {
            "type": "function",
            "function": {
              "name": "add_memory",
              "description": "Add or update a long-term memory entry so it is available in future sessions. Use this whenever the user shares something important to remember (name, preferences, projects, goals, etc.).",
              "parameters": {
                "type": "object",
                "properties": {
                  "key": {
                    "type": "string",
                    "description": "Short identifier for the memory, e.g. 'user_name', 'preferred_language', 'current_project'."
                  },
                  "content": {
                    "type": "string",
                    "description": "The information to store."
                  }
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
              "description": "Search stored long-term memories for a given query. Useful when you need to recall specific information about the user.",
              "parameters": {
                "type": "object",
                "properties": {
                  "query": {
                    "type": "string",
                    "description": "Keyword or phrase to search for in stored memories."
                  }
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
              "description": "Retrieve every stored long-term memory entry. Call this at the start of a session to load full context.",
              "parameters": {
                "type": "object",
                "properties": {},
                "additionalProperties": false
              }
            }
          }
        ]
        """)!.AsArray();

    // ── Construction ───────────────────────────────────────────────────────────

    public AgentChat(MemoryService memoryService)
    {
        _memoryService = memoryService;
        (_apiUrl, _model) = ConfigureHttpClient();
    }

    private static (string, string) ConfigureHttpClient()
    {
        var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var azureKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var azureDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";

        // 1. Azure OpenAI
        if (!string.IsNullOrWhiteSpace(azureEndpoint) && !string.IsNullOrWhiteSpace(azureKey))
        {
            s_http.DefaultRequestHeaders.Remove("api-key");
            s_http.DefaultRequestHeaders.Add("api-key", azureKey);
            var url = $"{azureEndpoint.TrimEnd('/')}/openai/deployments/{azureDeployment}/chat/completions?api-version=2024-10-21";
            return (url, azureDeployment);
        }

        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var openAiModel = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";
        var openAiBaseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");

        // 2. Any OpenAI-compatible endpoint (Ollama, llama.cpp, LM Studio, remote proxies…)
        //    OPENAI_API_KEY is optional for local servers that don't validate the key.
        //    If your server validates the key, set OPENAI_API_KEY to the expected value.
        if (!string.IsNullOrWhiteSpace(openAiBaseUrl))
        {
            var apiKey = !string.IsNullOrWhiteSpace(openAiKey) ? openAiKey : "none";
            s_http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
            var url = $"{openAiBaseUrl.TrimEnd('/')}/chat/completions";
            return (url, openAiModel);
        }

        // 3. Standard OpenAI
        if (!string.IsNullOrWhiteSpace(openAiKey))
        {
            s_http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", openAiKey);
            return ("https://api.openai.com/v1/chat/completions", openAiModel);
        }

        throw new InvalidOperationException(
            "No LLM credentials found. Set one of:\n" +
            "  • AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY  (Azure OpenAI)\n" +
            "  • OPENAI_BASE_URL [+ OPENAI_API_KEY]            (any OpenAI-compatible endpoint, e.g. Ollama / llama.cpp)\n" +
            "  • OPENAI_API_KEY                                 (OpenAI)");
    }

    // ── System prompt with injected long-term memory ───────────────────────────

    private string BuildSystemPrompt() => $"""
        You are a helpful, proactive AI assistant with persistent long-term memory.

        ## Long-Term Memory (injected from previous sessions)
        {_memoryService.FormatMemoriesForContext()}

        ## Guidelines
        - Use stored memories to personalise every response.
        - When the user shares something worth remembering (name, preferences, projects, decisions),
          call `add_memory` to persist it for future sessions.
        - When you need specific recalled information, use `search_memory` to look it up.
        - Be conversational, concise and helpful.
        """;

    // ── Tool execution ─────────────────────────────────────────────────────────

    private string ExecuteTool(string toolName, string toolArgumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolArgumentsJson);
            var root = doc.RootElement;

            return toolName switch
            {
                "add_memory" => _memoryService.AddOrUpdateMemory(
                    GetStringArg(root, "key"),
                    GetStringArg(root, "content")),

                "search_memory" => FormatSearchResults(
                    _memoryService.SearchMemories(GetStringArg(root, "query"))),

                "get_all_memories" => _memoryService.FormatMemoriesForContext(),

                _ => $"Unknown tool: '{toolName}'.",
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
            ["model"] = _model,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
            ["tools"] = s_tools.DeepClone(),
            ["tool_choice"] = "auto",
        };

        var body = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await s_http.PostAsync(_apiUrl, body);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        return (await JsonNode.ParseAsync(stream))!.AsObject();
    }

    // ── Agentic tool-call loop ─────────────────────────────────────────────────

    /// <summary>
    /// Runs the model against <paramref name="messages"/>, automatically
    /// executing any tool calls until the model produces a final text response.
    /// </summary>
    private async Task<string> RunAgentLoopAsync(List<JsonObject> messages)
    {
        while (true)
        {
            var response = await PostChatCompletionAsync(messages);
            var choice = response["choices"]![0]!;
            var finishReason = choice["finish_reason"]?.GetValue<string>();
            var message = choice["message"]!.AsObject();

            if (finishReason == "tool_calls")
            {
                // Record the assistant message (with embedded tool calls)
                messages.Add(message.DeepClone().AsObject());

                foreach (var toolCallNode in message["tool_calls"]!.AsArray())
                {
                    var toolCall = toolCallNode!.AsObject();
                    var id = toolCall["id"]!.GetValue<string>();
                    var functionName = toolCall["function"]!["name"]!.GetValue<string>();
                    var functionArgs = toolCall["function"]!["arguments"]!.GetValue<string>();

                    PrintToolCall(functionName, functionArgs);
                    var result = ExecuteTool(functionName, functionArgs);

                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = id,
                        ["content"] = result,
                    });
                }
            }
            else
            {
                return message["content"]?.GetValue<string>() ?? string.Empty;
            }
        }
    }

    // ── Short-term history helpers ─────────────────────────────────────────────

    private static JsonObject SystemMessage(string content) =>
        new() { ["role"] = "system", ["content"] = content };

    private static JsonObject UserMessage(string content) =>
        new() { ["role"] = "user", ["content"] = content };

    private static JsonObject AssistantMessage(string content) =>
        new() { ["role"] = "assistant", ["content"] = content };

    private void AddToHistory(JsonObject message)
    {
        _history.Add(message);
        // Trim to keep the sliding window within bounds
        while (_history.Count > MaxShortTermMessages)
            _history.RemoveAt(0);
    }

    private List<JsonObject> BuildMessages() =>
        [SystemMessage(BuildSystemPrompt()), .. _history];

    // ── Startup context-loading (RAG-like injection) ───────────────────────────

    /// <summary>
    /// Fires tool calls before the first user message to load long-term context
    /// into the session.  The resulting greeting is added to short-term history
    /// so it informs the rest of the conversation.
    /// </summary>
    private async Task InitializeSessionAsync()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("[Loading session context from long-term memory…]");
        Console.ResetColor();

        var initMessages = new List<JsonObject>
        {
            SystemMessage(BuildSystemPrompt()),
            UserMessage(
                "Before we begin, call get_all_memories to review everything you know " +
                "about me, then give a short, friendly greeting that shows you remember " +
                "relevant context. If no memories exist, just say hello."),
        };

        var greeting = await RunAgentLoopAsync(initMessages);

        if (!string.IsNullOrWhiteSpace(greeting))
        {
            // Inject greeting into short-term history so subsequent turns see it
            AddToHistory(AssistantMessage(greeting));

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("Assistant: ");
            Console.ResetColor();
            Console.WriteLine(greeting);
            Console.WriteLine();
        }
    }

    // ── Main chat turn ─────────────────────────────────────────────────────────

    private async Task<string> ChatAsync(string userMessage)
    {
        AddToHistory(UserMessage(userMessage));

        var messages = BuildMessages();
        var reply = await RunAgentLoopAsync(messages);

        AddToHistory(AssistantMessage(reply));
        return reply;
    }

    // ── Public entry point ─────────────────────────────────────────────────────

    public async Task RunAsync()
    {
        PrintBanner();

        // ── Startup: fire tool calls and inject context (RAG-like) ─────────────
        await InitializeSessionAsync();

        // ── Main REPL loop ──────────────────────────────────────────────────────
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("You: ");
            Console.ResetColor();

            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input)) continue;

            if (input.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Goodbye! Your memories have been saved.");
                break;
            }

            if (input.Equals("memory", StringComparison.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("── Stored Memories ──────────────────────────────────────────");
                Console.WriteLine(_memoryService.FormatMemoriesForContext());
                Console.WriteLine("─────────────────────────────────────────────────────────────");
                Console.ResetColor();
                continue;
            }

            if (input.Equals("history", StringComparison.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"── Short-Term History ({_history.Count}/{MaxShortTermMessages} messages) ──");
                foreach (var msg in _history)
                    Console.WriteLine($"  [{msg["role"]?.GetValue<string>() ?? "?"}] {GetMessageText(msg)}");
                Console.WriteLine("─────────────────────────────────────────────────────────────");
                Console.ResetColor();
                continue;
            }

            try
            {
                var reply = await ChatAsync(input);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("Assistant: ");
                Console.ResetColor();
                Console.WriteLine(reply);
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════╗");
        Console.WriteLine("║         Agentic Chat App  (CMcC)          ║");
        Console.WriteLine("╠═══════════════════════════════════════════╣");
        Console.WriteLine("║  Commands:                                ║");
        Console.WriteLine("║    memory   – show stored memories        ║");
        Console.WriteLine("║    history  – show short-term history     ║");
        Console.WriteLine("║    exit     – quit                        ║");
        Console.WriteLine("╚═══════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintToolCall(string name, string args)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  ↳ [tool:{name}] {args}");
        Console.ResetColor();
    }

    private static string GetMessageText(JsonObject msg)
    {
        var content = msg["content"];
        return content?.GetValue<string>() ?? "(tool calls)";
    }
}
