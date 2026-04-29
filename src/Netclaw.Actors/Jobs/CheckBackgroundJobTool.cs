// -----------------------------------------------------------------------
// <copyright file="CheckBackgroundJobTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Jobs;

[NetclawTool("check_background_job",
    "Query the status of a background job or cancel it. Returns status, elapsed time, and output tail for running jobs; full result for completed jobs.",
    Grant = "shell")]
public sealed partial class CheckBackgroundJobTool : NetclawTool<CheckBackgroundJobTool.Params>
{
    public const string ToolName = "check_background_job";
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(15);

    private readonly IActorRef _jobManager;

    public record Params(
        [property: Description("The job ID returned when the background job was submitted.")]
        string JobId,
        [property: Description("Set to true to cancel the running job.")]
        bool Cancel = false);

    public CheckBackgroundJobTool(IActorRef jobManager)
    {
        _jobManager = jobManager;
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.JobId))
            return "Error: job_id is required.";

        var jobId = new BackgroundJobId(args.JobId);
        var sessionId = context.SessionId ?? "";
        var audience = TrustAudience.Personal;
        if (!string.IsNullOrEmpty(context.Audience))
            Enum.TryParse(context.Audience, true, out audience);
        var boundary = context.Boundary ?? SecurityPolicyDefaults.PersonalBoundary;

        if (args.Cancel)
        {
            var cancel = await _jobManager.Ask<BackgroundJobCancelResponse>(
                new CancelBackgroundJob(jobId, new SessionId(sessionId), audience, boundary),
                AskTimeout, ct);
            return cancel.Found
                ? $"Cancellation request sent for job {args.JobId}."
                : $"Error: job {args.JobId} not found or not accessible from this session.";
        }

        var response = await _jobManager.Ask<BackgroundJobStatusResponse>(
            new QueryBackgroundJob(jobId, new SessionId(sessionId), audience, boundary),
            AskTimeout, ct);

        if (!response.Found)
            return $"Error: job {args.JobId} not found or not accessible from this session.";

        var status = response.Status.ToString().ToLowerInvariant();
        var elapsed = response.Elapsed?.TotalSeconds ?? 0;
        var result = $"Job {args.JobId}: {status} ({elapsed:F1}s elapsed)";

        if (!string.IsNullOrEmpty(response.Rationale))
            result += $"\nRationale: {response.Rationale}";

        if (response.ExitCode is not null)
            result += $"\nExit code: {response.ExitCode}";

        if (!string.IsNullOrEmpty(response.OutputTail))
            result += $"\nOutput (last {Math.Min(response.OutputTail.Length, BackgroundJobManagerActor.MaxOutputTailChars)} chars):\n```\n{response.OutputTail}\n```";

        if (!string.IsNullOrEmpty(response.OutputFilePath))
            result += $"\nFull output: {response.OutputFilePath}";

        return result;
    }
}
