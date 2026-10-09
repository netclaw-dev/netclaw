// -----------------------------------------------------------------------
// <copyright file="ChatClientProtocol.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Daemon;

internal static class ChatClientProtocol
{
    internal abstract record Request(CancellationToken Token)
    {
        internal abstract Task Completion { get; }
        internal abstract void Fail(Exception error);
        internal abstract void Cancel();
    }

    internal abstract record Command(CancellationToken Token) : Request(Token)
    {
        internal TaskCompletionSource Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override Task Completion => Reply.Task;
        internal override void Fail(Exception error) { Reply.TrySetException(error); _ = Reply.Task.Exception; }
        internal override void Cancel() => Reply.TrySetCanceled(Token);
    }

    internal abstract record SessionRequest(ChannelType Channel, CancellationToken Token) : Request(Token)
    {
        internal TaskCompletionSource<SessionId> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override Task Completion => Reply.Task;
        internal override void Fail(Exception error) { Reply.TrySetException(error); _ = Reply.Task.Exception; }
        internal override void Cancel() => Reply.TrySetCanceled(Token);
    }

    internal sealed record Connect(CancellationToken Token) : Command(Token);
    internal sealed record Open(SessionId? ResumeId, string? Initial) : Command(CancellationToken.None);
    internal sealed record Create(ChannelType Channel, CancellationToken Token) : SessionRequest(Channel, Token);
    internal sealed record Keep(ChannelType Channel, CancellationToken Token) : SessionRequest(Channel, Token);
    internal sealed record Resume(SessionId SessionId, ChannelType Channel, CancellationToken Token) : SessionRequest(Channel, Token);
    internal sealed record SendText(string Text, CancellationToken Token) : Command(Token);
    internal sealed record Respond(ToolCallId CallId, ApprovalOptionKey Selection, CancellationToken Token) : Command(Token);
    internal sealed record Recover() : Command(CancellationToken.None);
    internal sealed record Close(TaskCompletionSource<ChatCloseReceipt> Reply);

    internal abstract record ClientEvent;
    internal sealed record Output(SessionOutput Value) : ClientEvent;
    internal sealed record ConnectionChanged(DaemonConnectionEvent Value) : ClientEvent;

    internal abstract record TransportEvent;
    internal sealed record OutputReceived(SessionOutput Value) : TransportEvent;
    internal sealed record TransportDropped(Exception? Error) : TransportEvent;
    internal sealed record SessionBinding(SessionId SessionId, int AdmissionVersion);

    internal abstract record Completed(long Id);
    internal sealed record Connected(long Id) : Completed(Id);
    internal sealed record Bound(long Id, SessionBinding Binding) : Completed(Id);
    internal sealed record Accepted(long Id) : Completed(Id);
    internal sealed record Failed(long Id, Exception Error) : Completed(Id);
    internal sealed record Cancelled(long Id);
    internal sealed record Deadline(long Id);
    internal sealed record Retry;
    internal sealed record CloseDeadline;
}
