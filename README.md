# CMcC — Agentic Chat App

An interactive agentic chat application built on Azure OpenAI / OpenAI that demonstrates:

- **Short-term history** – a sliding-window conversation buffer keeps the last 20 messages in context, so the model always has recent conversational context without ever overflowing the token window.
- **Long-term memory** – the model can call built-in tools (`add_memory`, `search_memory`, `get_all_memories`) to persist and recall facts across sessions. Memories are stored locally in a JSON file.
- **Memory injection** – every system prompt is dynamically rebuilt to include the current long-term memory store, giving the model a RAG-like personalised context on every turn.
- **Startup context loading** – before the first user message the agent fires tool calls to load its long-term memory and generates a context-aware greeting, simulating RAG data-injection at session start.

---

## Quick Start

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- An **Azure OpenAI** resource (recommended) **or** an **OpenAI** API key

### 1. Configure credentials

Set the following environment variables (copy `AgenticChat/appsettings.example.json` as a reference):

**Azure OpenAI**
```bash
export AZURE_OPENAI_ENDPOINT="https://<your-resource>.openai.azure.com/"
export AZURE_OPENAI_API_KEY="<your-key>"
export AZURE_OPENAI_DEPLOYMENT="gpt-4o"   # optional, defaults to gpt-4o
```

**Standard OpenAI**
```bash
export OPENAI_API_KEY="sk-..."
export OPENAI_MODEL="gpt-4o"              # optional, defaults to gpt-4o
```

**Memory file location** (optional, defaults to `memory.json` in the working directory):
```bash
export MEMORY_FILE_PATH="/path/to/memory.json"
```

### 2. Run

```bash
cd AgenticChat
dotnet run
```

### 3. Chat commands

| Command   | Description                              |
|-----------|------------------------------------------|
| `memory`  | Display all stored long-term memories    |
| `history` | Show the current short-term chat history |
| `exit`    | Quit (memories are persisted to disk)    |

---

## Architecture

```
Program.cs
 └─ AgentChat (Services/AgentChat.cs)
     ├─ MemoryService (Services/MemoryService.cs)   ← JSON-backed long-term store
     ├─ Short-term history []                        ← sliding window (20 msgs)
     ├─ System prompt builder                        ← injects memories on every turn
     ├─ Tool: add_memory(key, content)               ← persist a memory
     ├─ Tool: search_memory(query)                   ← search memories (semantic-lite)
     ├─ Tool: get_all_memories()                     ← load all memories (used at startup)
     └─ Agentic loop                                 ← handles multi-step tool calls
```

### How the agentic loop works

1. User input is appended to the short-term history.
2. A full message list is assembled: `[system_prompt_with_memories] + history`.
3. The model responds; if it requests tool calls they are executed and fed back into the messages list. Step 3 repeats until the model emits a final text response.
4. The assistant reply is appended to history and printed.

### Startup sequence (RAG-like injection)

On launch, before the first user message, the agent:
1. Sends a startup prompt instructing the model to call `get_all_memories`.
2. Executes the tool call and returns the full memory store to the model.
3. The model produces a personalised greeting from the loaded context.
4. The greeting is stored as the first entry in short-term history.

