// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Netclaw.Actors.Channels;
using Netclaw.Actors.SubAgents;
using Netclaw.Cli.Daemon;
using static Netclaw.Actors.Sessions.SessionProtocol;

if (args.Length != 4 || !int.TryParse(args[1], out var timeoutSeconds) || !int.TryParse(args[2], out var requiredChildren))
{
    Console.Error.WriteLine("Usage: coordination-observer <daemon-endpoint> <timeout-seconds> <required-children> <prompt>");
    return 2;
}

var outputs = Channel.CreateUnbounded<SessionOutput>();
var calls = new List<object>();
var currentResponse = new StringBuilder();
var firstResponse = "";
var finalResponse = "";
var sessionIdValue = "";
var startedChildren = 0;
var completedChildren = 0;
var turnCompletions = 0;
var activeChildrenAtFirstCompletion = 0;
var receivedTextDelta = false;

await using var client = new DaemonClient(args[0]);
using var subscription = client.SessionOutput.Subscribe(output => outputs.Writer.TryWrite(output));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
var cancellationToken = timeout.Token;

try
{
    await client.ConnectAsync(cancellationToken);
    var sessionId = await client.CreateSessionAsync(ChannelType.Headless, cancellationToken);
    sessionIdValue = sessionId;
    await client.SendAsync(
        args[3],
        cancellationToken);

    var completed = false;
    while (!completed)
    {
        var output = await outputs.Reader.ReadAsync(cancellationToken);
        switch (output)
        {
            case TextDeltaOutput delta:
                currentResponse.Append(delta.Delta);
                receivedTextDelta = true;
                break;
            case TextOutput text when !receivedTextDelta:
                currentResponse.Append(text.Text);
                break;
            case ToolCallOutput call:
                calls.Add(new
                {
                    callId = call.CallId.Value,
                    toolName = call.ToolName.Value,
                    argumentsJson = call.ArgumentsJson
                });
                break;
            case SubAgentOutput child when child.Phase == SubAgentPhase.Started:
                startedChildren++;
                break;
            case SubAgentOutput child when child.Phase == SubAgentPhase.Completed:
                completedChildren++;
                break;
            case TurnCompleted:
                turnCompletions++;
                finalResponse = currentResponse.ToString();
                if (turnCompletions == 1)
                {
                    firstResponse = finalResponse;
                    activeChildrenAtFirstCompletion = startedChildren - completedChildren;
                }
                currentResponse.Clear();
                receivedTextDelta = false;
                if (turnCompletions > 1 && completedChildren >= requiredChildren)
                    completed = true;
                break;
            case ErrorOutput error:
                throw new InvalidOperationException(error.Message);
        }
    }
}
catch (OperationCanceledException) when (timeout.IsCancellationRequested)
{
    finalResponse = currentResponse.ToString();
}

var automaticContinuation = turnCompletions > 1 && completedChildren >= requiredChildren;
var envelope = new
{
    sessionId = sessionIdValue,
    response = finalResponse,
    toolCalls = calls,
    coordination = new
    {
        initialResponse = firstResponse,
        activeChildrenAtFirstCompletion,
        completedChildren,
        turnCompletions,
        userMessagesSent = 1,
        automaticContinuation
    }
};
Console.WriteLine(JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
return activeChildrenAtFirstCompletion > 0 && automaticContinuation ? 0 : 1;
