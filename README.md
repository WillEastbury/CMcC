# CMcC — Agentic Chat App

An interactive agentic chat application that works with **any OpenAI-compatible LLM backend** — cloud services, remote proxies, or fully local inference (e.g. Ollama, llama.cpp, LM Studio with GGUF models). It demonstrates:

- **Short-term history** – a sliding-window conversation buffer keeps the last 20 messages in context, so the model always has recent conversational context without ever overflowing the token window.
- **Long-term memory** – the model can call built-in tools (`add_memory`, `search_memory`, `get_all_memories`) to persist and recall facts across sessions. Memories are stored locally in a JSON file.
- **Memory injection** – every system prompt is dynamically rebuilt to include the current long-term memory store, giving the model a RAG-like personalised context on every turn.
- **Startup context loading** – before the first user message the agent fires tool calls to load its long-term memory and generates a context-aware greeting, simulating RAG data-injection at session start.

---

## Quick Start

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- One of the supported LLM backends (see below)

### 1. Configure your LLM backend

Set **one** of the following groups of environment variables (copy `AgenticChat/appsettings.example.json` as a reference):

**Option A — Azure OpenAI**
```bash
export AZURE_OPENAI_ENDPOINT="https://<your-resource>.openai.azure.com/"
export AZURE_OPENAI_API_KEY="<your-key>"
export AZURE_OPENAI_DEPLOYMENT="gpt-4o"   # optional, defaults to gpt-4o
```

**Option B — Any OpenAI-compatible endpoint (local or remote)**

Use this for local inference with GGUF models via [Ollama](https://ollama.com),
[llama.cpp server](https://github.com/ggerganov/llama.cpp), [LM Studio](https://lmstudio.ai),
or any other OpenAI-compatible proxy / API.

```bash
# Ollama (local)
export OPENAI_BASE_URL="http://localhost:11434/v1"
export OPENAI_MODEL="llama3"          # model tag served by Ollama
# OPENAI_API_KEY is optional for local servers that don't validate the key.
# Set it to the expected value if your server requires authentication.

# llama.cpp server (local)
export OPENAI_BASE_URL="http://localhost:8080/v1"
export OPENAI_MODEL="llama3"

# LM Studio (local)
export OPENAI_BASE_URL="http://localhost:1234/v1"
export OPENAI_MODEL="llama3"

# Remote proxy (e.g. self-hosted or third-party)
export OPENAI_BASE_URL="https://my-proxy.example.com/v1"
export OPENAI_API_KEY="<proxy-api-key>"
export OPENAI_MODEL="my-model"
```

**Option C — Standard OpenAI**
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

### LLM backend selection (priority order)

| Priority | Condition | Backend |
|----------|-----------|---------|
| 1 | `AZURE_OPENAI_ENDPOINT` **and** `AZURE_OPENAI_API_KEY` are set | Azure OpenAI |
| 2 | `OPENAI_BASE_URL` is set | Any OpenAI-compatible endpoint (local or remote) |
| 3 | `OPENAI_API_KEY` is set | Standard OpenAI |

No additional dependencies or SDKs are required for local inference — the existing
`OpenAI` .NET SDK handles every backend through its standard chat-completions interface.

### How the agentic loop works

1. User input is appended to the short-term history.
2. A full message list is assembled: `[system_prompt_with_memories] + history`.
3. The model responds; if it requests tool calls they are executed and fed back into the messages list. Step 3 repeats until the model emits a final text response.
4. The assistant reply is appended to history and printed.

### Startup sequence (RAG-like injection and MCP Calls

On launch, before the first user message, the agent:
1. Sends a startup prompt instructing the model to call `get_all_memories`.
2. Executes the tool call and returns the full memory store to the model.
3. The model produces a personalised greeting from the loaded context.
4. The greeting is stored as the first entry in short-term history.

# CMcC
An example of creating an interactive agent that can consume / ground in multiple live data sources based upon configuration.

## Idea
The agent should simply have some grounding from an API before the chat context even starts - this makes the need for that data and the prompt to be hyper-tuned and the framework here should show that. 

LLM API -> Context starts by generating parameters for the query. 
There are thousands of possible use cases for this - sports analysis, holiday booking, agentic commerce, documentation searches and context. WorkIQ etc. 

   
