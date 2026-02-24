# SignalR IHubContext Pattern

## Problem

SignalR hubs are **transient** — a new instance per method invocation. You
cannot store state on the hub class or call hub methods from outside the hub
(e.g., from an Akka.Streams callback on a thread pool thread).

## Solution: IHubContext<THub, TClient>

Inject `IHubContext<THub, TClient>` into a **singleton** service. This gives
thread-safe access to `Clients.Client(connectionId)` without needing a hub
instance.

### Architecture

```
SessionHub (transient, per-invocation)
    │  captures Context.ConnectionId
    └→ SessionRegistry (singleton)
           │  injects IHubContext<SessionHub, ISessionHubClient>
           └→ SessionPipeline.CreateAsync() → MaterializedSession
                   ├── Output → Sink.ForEach → hubContext.Clients.Client(connId).ReceiveOutput(dto)
                   └── Input ← Source.Queue ← SendMessage()
```

### Key Files

- `src/Netclaw.Daemon/Gateway/ISessionHubClient.cs` — strongly-typed client interface
- `src/Netclaw.Daemon/Gateway/SessionRegistry.cs` — singleton owning session state + IHubContext
- `src/Netclaw.Daemon/Gateway/SessionHub.cs` — typed hub delegating to registry

### Registration

```csharp
builder.Services.AddSignalR();
builder.Services.AddSingleton<SessionRegistry>();
```

### Rules

- **Never store state on the hub.** Use a singleton service with concurrent collections.
- **Never inject IHubContext into the hub itself.** The hub already has `Clients` — use
  `IHubContext` in services that need to push messages from outside hub method calls.
- **Fire-and-forget from stream callbacks.** `IHubContext.Clients.Client(id).Method()`
  returns `Task` but the Akka.Streams `Sink.ForEach` action is synchronous — use
  `_ = hubContext.Clients.Client(connId).ReceiveOutput(dto)` to avoid blocking the stream.
- **Clean up on disconnect.** Override `OnDisconnectedAsync` to remove connection mappings
  and dispose stream resources.
