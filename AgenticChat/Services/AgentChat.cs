using System.ClientModel;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using OpenAI;
using OpenAI.Chat;
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
    private readonly ChatClient _chatClient;

    // ── Short-term history (user + assistant pairs only) ───────────────────────
    private readonly List<ChatMessage> _history = [];

    // ── Tool definitions ───────────────────────────────────────────────────────

    private static readonly ChatTool s_addMemoryTool = ChatTool.CreateFunctionTool(
        functionName: "add_memory",
        functionDescription:
            "Add or update a long-term memory entry so it is available in future sessions. " +
            "Use this whenever the user shares something important to remember " +
            "(name, preferences, projects, goals, etc.).",
        functionParameters: BinaryData.FromString("""
        {
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
        """));

    private static readonly ChatTool s_searchMemoryTool = ChatTool.CreateFunctionTool(
        functionName: "search_memory",
        functionDescription:
            "Search stored long-term memories for a given query. " +
            "Useful when you need to recall specific information about the user.",
        functionParameters: BinaryData.FromString("""
        {
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
        """));

    private static readonly ChatTool s_getAllMemoriesTool = ChatTool.CreateFunctionTool(
        functionName: "get_all_memories",
        functionDescription:
            "Retrieve every stored long-term memory entry. " +
            "Call this at the start of a session to load full context.",
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {},
            "additionalProperties": false
        }
        """));

    private static readonly IReadOnlyList<ChatTool> s_tools =
        [s_addMemoryTool, s_searchMemoryTool, s_getAllMemoriesTool];

    // ── Construction ───────────────────────────────────────────────────────────

    public AgentChat(MemoryService memoryService)
    {
        _memoryService = memoryService;
        _chatClient = BuildChatClient();
    }

    private static ChatClient BuildChatClient()
    {
        var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var azureKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var azureDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";

        // 1. Azure OpenAI
        if (!string.IsNullOrWhiteSpace(azureEndpoint) && !string.IsNullOrWhiteSpace(azureKey))
        {
            var azureClient = new AzureOpenAIClient(
                new Uri(azureEndpoint),
                new AzureKeyCredential(azureKey));
            return azureClient.GetChatClient(azureDeployment);
        }

        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var openAiModel = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";
        var openAiBaseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");

        // 2. Any OpenAI-compatible endpoint (Ollama, llama.cpp, LM Studio, remote proxies…)
        //    OPENAI_API_KEY is optional for local servers that don't validate the key.
        //    ApiKeyCredential requires a non-empty value, so we fall back to "none".
        //    If your server validates the key, set OPENAI_API_KEY to the expected value.
        if (!string.IsNullOrWhiteSpace(openAiBaseUrl))
        {
            var apiKey = !string.IsNullOrWhiteSpace(openAiKey) ? openAiKey : "none";
            var options = new OpenAIClientOptions { Endpoint = new Uri(openAiBaseUrl) };
            var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
            return client.GetChatClient(openAiModel);
        }

        // 3. Standard OpenAI
        if (!string.IsNullOrWhiteSpace(openAiKey))
        {
            var openAiClient = new OpenAIClient(new ApiKeyCredential(openAiKey));
            return openAiClient.GetChatClient(openAiModel);
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

    // ── Agentic tool-call loop ─────────────────────────────────────────────────

    /// <summary>
    /// Runs the model against <paramref name="messages"/>, automatically
    /// executing any tool calls until the model produces a final text response.
    /// </summary>
    private async Task<string> RunAgentLoopAsync(List<ChatMessage> messages)
    {
        var options = new ChatCompletionOptions();
        foreach (var tool in s_tools) options.Tools.Add(tool);

        while (true)
        {
            var response = await _chatClient.CompleteChatAsync(messages, options);
            var completion = response.Value;

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                // Record the assistant message (with embedded tool calls)
                messages.Add(new AssistantChatMessage(completion));

                foreach (var toolCall in completion.ToolCalls)
                {
                    PrintToolCall(toolCall.FunctionName, toolCall.FunctionArguments.ToString());
                    var result = ExecuteTool(
                        toolCall.FunctionName,
                        toolCall.FunctionArguments.ToString());
                    messages.Add(new ToolChatMessage(toolCall.Id, result));
                }
            }
            else
            {
                return completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;
            }
        }
    }

    // ── Short-term history helpers ─────────────────────────────────────────────

    private void AddToHistory(ChatMessage message)
    {
        _history.Add(message);
        // Trim to keep the sliding window within bounds
        while (_history.Count > MaxShortTermMessages)
            _history.RemoveAt(0);
    }

    private List<ChatMessage> BuildMessages() =>
        [new SystemChatMessage(BuildSystemPrompt()), .. _history];

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

        var initMessages = new List<ChatMessage>
        {
            new SystemChatMessage(BuildSystemPrompt()),
            new UserChatMessage(
                "Before we begin, call get_all_memories to review everything you know " +
                "about me, then give a short, friendly greeting that shows you remember " +
                "relevant context. If no memories exist, just say hello."),
        };

        var greeting = await RunAgentLoopAsync(initMessages);

        if (!string.IsNullOrWhiteSpace(greeting))
        {
            // Inject greeting into short-term history so subsequent turns see it
            AddToHistory(new AssistantChatMessage(greeting));

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
        AddToHistory(new UserChatMessage(userMessage));

        var messages = BuildMessages();
        var reply = await RunAgentLoopAsync(messages);

        AddToHistory(new AssistantChatMessage(reply));
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
                    Console.WriteLine($"  [{msg.GetType().Name.Replace("ChatMessage", "")}] {GetMessageText(msg)}");
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

    private static string GetMessageText(ChatMessage msg) => msg switch
    {
        UserChatMessage u => u.Content.FirstOrDefault()?.Text ?? "(empty)",
        AssistantChatMessage a => a.Content.FirstOrDefault()?.Text ?? "(tool calls)",
        _ => "(other)",
    };
}
