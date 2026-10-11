## Why

The chat client divides connection, attachment, retries, and message order between two lifecycle owners.
The hub returns success before the session journals the input, so a normal quit can precede admission.

Source PRDs: `PRD-001`, `PRD-004`, `PRD-009`.

## What Changes

- Await the existing session acknowledgment through the real SignalR input pipeline.
- Add explicit text admission support to the existing session response; reject unsupported support before text dispatch.
- Replace the `DaemonClient` command loop and the chat lifecycle paths with one local actor.
- Use `ReceiveActor` behaviors through `Become(...)` for explicit state transitions.
- Connect a new interactive chat without creating a session until its first input.
- Attach a resumed chat when its page opens.
- Reuse the wizard generation check before identity redo enters guided chat.
- Use one ordered input queue and one retry schedule.
- Wait asynchronously for admission for at most two seconds after a normal quit.
- Report queued inputs as unsent and an interrupted input RPC as delivery unconfirmed.
- Retain the public daemon client API, authentication, event isolation, and explicit headless session creation.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-cli`: Add chat lifecycle, input order, delivery status, and bounded close requirements.
- `session-resume`: Add SignalR input admission and disconnect requirements.

## Impact

The implementation affects the CLI client, its TUI adapter, the SignalR gateway, and their tests.
The CLI already references Akka.NET through `Netclaw.Actors`.
The local actor system owns client control only; the daemon retains model work, tools, and persistence.
The implementation preserves the hub method signatures and durable record formats.

The canonical plan contains the design, Delivery section, tasks, and proof procedures:
[Chat client local FSM plan](../../../.systematize/plans/chat-client-local-fsm/plan.html).

## Scope

MVP scope includes the existing chat, resume, init-to-chat, reminder-to-chat, and headless consumers.
The change does not add remoting, clustering, client persistence, a new config property, or a replay protocol.
It does not delete sessions with zero completed turns.
Draft [PR #2413](https://github.com/netclaw-dev/netclaw/pull/2413) supplies prior evidence; it is not the implementation base.

## Security and Operational Impact

The registry retains connection attachment, authenticated identity, and ingress checks before input dispatch.
The successful text SendMessage response proves daemon admission. It does not prove model completion or tool permission.
The client does not retry an input after an uncertain RPC result.
Offline commands do not start an actor system.
The implementation must update the operations skill and run the applicable eval and native smoke gates.
The user authorizes implementation and local verification. Remote publication, merge, and deployment require their own authority.
