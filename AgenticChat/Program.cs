using AgenticChat.Services;

var sessionsDir = Environment.GetEnvironmentVariable("SESSIONS_DIR") ?? "sessions";
var memoriesDir = Environment.GetEnvironmentVariable("MEMORY_DIR") ?? "memories";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => new SessionService(sessionsDir));
builder.Services.AddSingleton(sp =>
    new AgentChatService(sp.GetRequiredService<SessionService>(), memoriesDir));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// ── Helper ─────────────────────────────────────────────────────────────────────
static bool IsValidGuid(string? s) =>
    Guid.TryParse(s, out _);

// ── API endpoints ──────────────────────────────────────────────────────────────

// List all sessions for a user
app.MapGet("/api/sessions/{userGuid}", (string userGuid, SessionService sessions) =>
{
    if (!IsValidGuid(userGuid)) return Results.BadRequest("Invalid user GUID.");
    var list = sessions.GetUserSessions(userGuid)
                       .Select(s => new { s.Id, s.Title, s.UpdatedAt, MessageCount = s.Messages.Count });
    return Results.Ok(list);
});

// Create a new session (fires greeting via LLM)
app.MapPost("/api/sessions/{userGuid}", async (string userGuid, AgentChatService agent) =>
{
    if (!IsValidGuid(userGuid)) return Results.BadRequest("Invalid user GUID.");
    var session = await agent.CreateSessionAsync(userGuid);
    return Results.Ok(session);
});

// Get full session (with all messages)
app.MapGet("/api/sessions/{userGuid}/{sessionId}", (
    string userGuid, string sessionId, SessionService sessions) =>
{
    if (!IsValidGuid(userGuid) || !IsValidGuid(sessionId))
        return Results.BadRequest("Invalid GUID.");

    var session = sessions.GetSession(userGuid, sessionId);
    return session is null ? Results.NotFound() : Results.Ok(session);
});

// Send a message and get a reply
app.MapPost("/api/sessions/{userGuid}/{sessionId}/messages", async (
    string userGuid, string sessionId,
    MessageRequest req,
    AgentChatService agent) =>
{
    if (!IsValidGuid(userGuid) || !IsValidGuid(sessionId))
        return Results.BadRequest("Invalid GUID.");

    if (string.IsNullOrWhiteSpace(req.Content))
        return Results.BadRequest("Message content must not be empty.");

    try
    {
        var (reply, title) = await agent.SendMessageAsync(userGuid, sessionId, req.Content);
        return Results.Ok(new { Content = reply, SessionTitle = title });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(ex.Message);
    }
});

// Serve the SPA for /chat/{guid} so the frontend can restore localStorage
app.MapFallbackToFile("index.html");

app.Run();

// ── Request DTOs ───────────────────────────────────────────────────────────────
record MessageRequest(string Content);

