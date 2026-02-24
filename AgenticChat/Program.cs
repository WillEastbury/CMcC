using AgenticChat.Services;

// Resolve memory file path from environment or default
var memoryFilePath = Environment.GetEnvironmentVariable("MEMORY_FILE_PATH") ?? "memory.json";

var memoryService = new MemoryService(memoryFilePath);
var sessionManager = new SessionManager();
var agent = new AgentChat(memoryService, sessionManager);

await agent.RunAsync();
