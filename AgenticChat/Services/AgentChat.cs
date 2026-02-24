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
///   <item>Multi-session management (create, list, switch named sessions)</item>
///   <item>Short-term conversational history (sliding window, per session)</item>
///   <item>Long-term memory tools that persist information across sessions</item>
///   <item>Memory injected into every system prompt (RAG-style)</item>
///   <item>A startup context-loading sequence that fires tool calls before the
///         first user message</item>
///   <item>Context viewer showing a tree/tabular snapshot of what the agent sees</item>
///   <item>Focus mode that directs agent attention to a specific memory topic</item>
/// </list>
/// </summary>
public class AgentChat
{
    // ── Constants ──────────────────────────────────────────────────────────────
    private const int MaxShortTermMessages = 20;
    private const int HistoryPreviewLength = 72;

    // ── Dependencies ───────────────────────────────────────────────────────────
    private readonly MemoryService _memoryService;
    private readonly SessionManager _sessionManager;
    private readonly ChatClient _chatClient;

    // ── Short-term history (delegates to the active session) ───────────────────
    private List<ChatMessage> History => _sessionManager.Active.History;

    // ── Pending focus directive (injected into the next user turn) ─────────────
    private string? _pendingFocus;

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

    public AgentChat(MemoryService memoryService, SessionManager sessionManager)
    {
        _memoryService = memoryService;
        _sessionManager = sessionManager;
        _chatClient = BuildChatClient();
    }

    private static ChatClient BuildChatClient()
    {
        var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var azureKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var azureDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";

        if (!string.IsNullOrWhiteSpace(azureEndpoint) && !string.IsNullOrWhiteSpace(azureKey))
        {
            var azureClient = new AzureOpenAIClient(
                new Uri(azureEndpoint),
                new AzureKeyCredential(azureKey));
            return azureClient.GetChatClient(azureDeployment);
        }

        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException(
                "No OpenAI credentials found. Set either " +
                "AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY (for Azure OpenAI) " +
                "or OPENAI_API_KEY (for OpenAI).");

        var openAiModel = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";
        var openAiClient = new OpenAIClient(new ApiKeyCredential(openAiKey));
        return openAiClient.GetChatClient(openAiModel);
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
        History.Add(message);
        // Trim to keep the sliding window within bounds
        while (History.Count > MaxShortTermMessages)
            History.RemoveAt(0);
        _sessionManager.Active.LastActiveAt = DateTime.UtcNow;
    }

    private List<ChatMessage> BuildMessages() =>
        [new SystemChatMessage(BuildSystemPrompt()), .. History];

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
        // Prepend any pending focus directive
        if (_pendingFocus is not null)
        {
            userMessage = $"[Focus context: {_pendingFocus}]\n\n{userMessage}";
            _pendingFocus = null;
        }

        AddToHistory(new UserChatMessage(userMessage));
        _sessionManager.Active.TurnCount++;

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
            var session = _sessionManager.Active;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"[{session.Name}] You");
            if (_pendingFocus is not null)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write($" (focus: {_pendingFocus})");
            }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(": ");
            Console.ResetColor();

            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input)) continue;

            // ── exit ───────────────────────────────────────────────────────────
            if (input.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Goodbye! Your memories have been saved.");
                break;
            }

            // ── memory ─────────────────────────────────────────────────────────
            if (input.Equals("memory", StringComparison.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("── Stored Memories ──────────────────────────────────────────");
                Console.WriteLine(_memoryService.FormatMemoriesForContext());
                Console.WriteLine("─────────────────────────────────────────────────────────────");
                Console.ResetColor();
                continue;
            }

            // ── history ────────────────────────────────────────────────────────
            if (input.Equals("history", StringComparison.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"── Short-Term History ({History.Count}/{MaxShortTermMessages} messages) ──");
                foreach (var msg in History)
                    Console.WriteLine($"  [{msg.GetType().Name.Replace("ChatMessage", "")}] {GetMessageText(msg)}");
                Console.WriteLine("─────────────────────────────────────────────────────────────");
                Console.ResetColor();
                continue;
            }

            // ── sessions ───────────────────────────────────────────────────────
            if (input.Equals("sessions", StringComparison.OrdinalIgnoreCase))
            {
                PrintSessionsTable();
                continue;
            }

            // ── new <name> ─────────────────────────────────────────────────────
            if (input.StartsWith("new ", StringComparison.OrdinalIgnoreCase))
            {
                var newName = input[4..].Trim();
                if (string.IsNullOrEmpty(newName))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Usage: new <session-name>");
                    Console.ResetColor();
                }
                else
                {
                    var newSession = _sessionManager.CreateSession(newName);
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"Created and switched to session '{newSession.Name}' [{newSession.Id}].");
                    Console.ResetColor();
                    await InitializeSessionAsync();
                }
                continue;
            }

            // ── switch <name|#> ────────────────────────────────────────────────
            if (input.StartsWith("switch ", StringComparison.OrdinalIgnoreCase))
            {
                var target = input[7..].Trim();
                var switched = _sessionManager.SwitchTo(target);
                Console.ForegroundColor = switched is not null ? ConsoleColor.Cyan : ConsoleColor.Red;
                Console.WriteLine(switched is not null
                    ? $"Switched to session '{switched.Name}' [{switched.Id}]  (turn {switched.TurnCount}, {switched.History.Count} messages)."
                    : $"Session '{target}' not found. Use 'sessions' to list available sessions.");
                Console.ResetColor();
                continue;
            }

            // ── context ────────────────────────────────────────────────────────
            if (input.Equals("context", StringComparison.OrdinalIgnoreCase))
            {
                PrintContextTree();
                continue;
            }

            // ── focus <topic> ──────────────────────────────────────────────────
            if (input.StartsWith("focus ", StringComparison.OrdinalIgnoreCase))
            {
                var topic = input[6..].Trim();
                if (string.IsNullOrEmpty(topic))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Usage: focus <topic>  – directs the agent to pay attention to that topic on the next turn.");
                    Console.ResetColor();
                }
                else
                {
                    _pendingFocus = topic;
                    var matches = _memoryService.SearchMemories(topic).ToList();
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine($"Focus set to '{topic}'. Relevant memories ({matches.Count}):");
                    foreach (var m in matches)
                        Console.WriteLine($"  ├─ [{m.Key}]: {m.Content}");
                    if (matches.Count == 0)
                        Console.WriteLine("  └─ (none – the agent will still be directed to focus on this topic)");
                    Console.ResetColor();
                }
                continue;
            }

            try
            {
                var reply = await ChatAsync(input);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"[{_sessionManager.Active.Name}] Assistant: ");
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
        Console.WriteLine("╔══════════════════════════════════════════════════╗");
        Console.WriteLine("║          Agentic Chat App  (CMcC)                ║");
        Console.WriteLine("╠══════════════════════════════════════════════════╣");
        Console.WriteLine("║  Commands:                                       ║");
        Console.WriteLine("║    memory            – show stored memories      ║");
        Console.WriteLine("║    history           – show short-term history   ║");
        Console.WriteLine("║    context           – context tree / data pane  ║");
        Console.WriteLine("║    sessions          – list all sessions         ║");
        Console.WriteLine("║    new <name>        – create a new session      ║");
        Console.WriteLine("║    switch <name|#>   – switch active session     ║");
        Console.WriteLine("║    focus <topic>     – direct agent attention    ║");
        Console.WriteLine("║    exit              – quit                      ║");
        Console.WriteLine("╚══════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints a tabular list of all sessions, highlighting the active one.
    /// </summary>
    private void PrintSessionsTable()
    {
        var sessions = _sessionManager.Sessions;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("── Sessions ─────────────────────────────────────────────────────────");
        Console.WriteLine($"  {"#",-4} {"Name",-20} {"Created",-21} {"Turns",-6} {"Msgs",-5} {"Status"}");
        Console.WriteLine($"  {"─",-4} {"─────────────────────",-20} {"───────────────────",-21} {"─────",-6} {"────",-5} ──────────");
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            bool isActive = s == _sessionManager.Active;
            Console.ForegroundColor = isActive ? ConsoleColor.White : ConsoleColor.Cyan;
            Console.WriteLine(
                $"  {i + 1,-4} {s.Name,-20} {s.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {s.TurnCount,-6} {s.History.Count,-5} {(isActive ? "← active" : "")}");
        }
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("─────────────────────────────────────────────────────────────────────");
        Console.ResetColor();
    }

    /// <summary>
    /// Prints a tree-style snapshot of everything the agent can currently see:
    /// the active session info, long-term memories, and short-term history.
    /// </summary>
    private void PrintContextTree()
    {
        var session = _sessionManager.Active;
        var memories = _memoryService.GetAllMemories().ToList();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine($"  CONTEXT SNAPSHOT  │  Session: {session.Name} [{session.Id}]  │  Turn: {session.TurnCount}");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine();

        // ── Long-term memory tree ──────────────────────────────────────────────
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  📦  Long-Term Memory  ({memories.Count} {(memories.Count == 1 ? "entry" : "entries")})");
        if (memories.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("      └─ (no memories stored yet)");
        }
        else
        {
            for (int i = 0; i < memories.Count; i++)
            {
                var m = memories[i];
                bool last = i == memories.Count - 1;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"  {(last ? "└─" : "├─")} ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"[{m.Key}]");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"  {m.Content}");
            }
        }

        Console.WriteLine();

        // ── Short-term history tree ────────────────────────────────────────────
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  💬  Conversation History  ({History.Count} / {MaxShortTermMessages} messages)");
        if (History.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("      └─ (no messages yet)");
        }
        else
        {
            for (int i = 0; i < History.Count; i++)
            {
                var msg = History[i];
                bool last = i == History.Count - 1;
                var role = msg switch
                {
                    UserChatMessage => "User     ",
                    AssistantChatMessage => "Assistant",
                    _ => "Other    ",
                };
                var text = GetMessageText(msg);
                var preview = text.Length > HistoryPreviewLength ? text[..HistoryPreviewLength] + "…" : text;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"  {(last ? "└─" : "├─")} ");
                Console.ForegroundColor = msg is UserChatMessage ? ConsoleColor.Green : ConsoleColor.White;
                Console.Write($"[{role}]");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"  {preview}");
            }
        }

        Console.WriteLine();

        // ── Pending focus ──────────────────────────────────────────────────────
        if (_pendingFocus is not null)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"  🎯  Pending Focus: {_pendingFocus}");
            Console.WriteLine();
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.ResetColor();
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
