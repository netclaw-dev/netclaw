---
name: accordant-async-operations
description: How to model background work, step functions, and polling - use this skill when testing async workflows, job queues, or operations that complete in the background
---

# Async Operations in Accordant

Some operations don't complete when the API call returns. You call an endpoint, it returns "Pending," and background work finishes later. Accordant models this with **step functions** and handles testing with **polling**.

## The Pattern

1. API returns immediately (status: Pending)
2. Background work happens asynchronously
3. Later API calls observe the completed state

```
PUT /api/jobs/job123 → { status: "Pending" }
// ... background processing ...
GET /api/jobs/job123 → { status: "Completed" }
```

## Modeling Background Work

Use `.Triggers()` with `AsyncOperation.Create`:

```csharp
[State]
public partial class JobQueueState
{
    public Dictionary<string, JobStatus> Jobs { get; set; } = new();
}

public enum JobStatus { Pending, Completed, Failed }

spec.Operation<string, ApiResult<Job>>("CreateJob", (jobId, state) =>
{
    if (state.Jobs.ContainsKey(jobId))
        return Expect.That<ApiResult<Job>>(r => r.IsConflict).SameState();

    return Expect.That<ApiResult<Job>>(r => r.IsSuccess && r.Data.Status == JobStatus.Pending)
           .ThenState<JobQueueState>(next => next.Jobs[jobId] = JobStatus.Pending)
           .Triggers(AsyncOperation.Create<JobQueueState>(
               isTerminal: s => s.Jobs[jobId] != JobStatus.Pending,
               transitions: new Action<JobQueueState>[] {
                   next => next.Jobs[jobId] = JobStatus.Completed,
                   next => next.Jobs[jobId] = JobStatus.Failed
               }
           ));
});
```

### Key Parameters

- **`isTerminal`**: Predicate returning true when background work is done
- **`transitions`**: Array of possible outcomes (non-determinism — job could complete or fail)

## How State Tracking Works

After `CreateJob` returns, the system could be in **either** state:
- `Jobs["job123"] = Pending` (still processing)
- `Jobs["job123"] = Completed` (already done)
- `Jobs["job123"] = Failed` (already failed)

Accordant tracks **all possibilities** until you observe a response that narrows it down.

### Observation Narrows Down State

```csharp
spec.Operation<string, ApiResult<Job>>("GetJob", (jobId, state) =>
{
    if (!state.Jobs.TryGetValue(jobId, out var status))
        return Expect.That<ApiResult<Job>>(r => r.IsNotFound).SameState();

    return Expect.That<ApiResult<Job>>(r => r.IsSuccess && r.Data.Status == status)
           .SameState();
});
```

When GetJob returns `{ status: "Completed" }`:
- States where status = Pending → Eliminated (doesn't match)
- States where status = Completed → Kept (matches!)
- States where status = Failed → Eliminated (doesn't match)

## Setting Up Polling

For test execution, configure polling to wait for background work:

### Using PollingSetup

```csharp
public class CreateJobOperation : Operation<string, ApiResult<Job>, JobQueueState>
{
    public override PollingSetup Polling => new PollingSetup
    {
        Operation = "GetJob",      // Which operation to poll
        WaitTimeInMs = 100,        // Delay between polls
        MaxRetryCount = 100        // Maximum attempts (liveness check)
    };

    public override ExpectedOutcomes Apply(string jobId, JobQueueState state)
    {
        // ... Apply logic with .Triggers() ...
    }
}
```

### How Polling Works

1. Execute `CreateJob` → Returns Pending
2. Wait `WaitTimeInMs`
3. Execute `GetJob` → Check if terminal
4. If not terminal, repeat from step 2
5. If terminal, continue to next operation
6. If `MaxRetryCount` exceeded → Liveness failure

## Liveness Testing

`MaxRetryCount` acts as a liveness check. If background work never completes:

```
Liveness violation: job still Pending after 100 retries
```

This catches "stuck" systems — a job that stays Pending forever is wrong, even if no individual response violates the spec.

## Manual Polling

If writing tests manually instead of using `spec.RunTests()`:

```csharp
var stateProfile = new StateProfile(new JobQueueState());

// Execute CreateJob
var createResponse = await client.CreateJob("job123");
(bool isValid, string message, stateProfile) =
    spec.Allows(spec.GetOperation("CreateJob"), "job123", createResponse, stateProfile);
Assert.IsTrue(isValid, message);

// Poll until complete
for (int i = 0; i < 100; i++)
{
    await Task.Delay(100);
    var getResponse = await client.GetJob("job123");
    (isValid, message, stateProfile) =
        spec.Allows(spec.GetOperation("GetJob"), "job123", getResponse, stateProfile);
    Assert.IsTrue(isValid, message);

    // Check if all possible states are terminal
    bool allTerminal = stateProfile.StatesAndStepFunctions
        .All(ssf => ((JobQueueState)ssf.State).Jobs["job123"] != JobStatus.Pending);

    if (allTerminal) break;
}
```

## Single Transition (Simpler Case)

When only one outcome is possible:

```csharp
.Triggers(AsyncOperation.Create<JobQueueState>(
    isTerminal: s => s.Jobs[jobId] == JobStatus.Completed,
    transition: next => next.Jobs[jobId] = JobStatus.Completed  // Single transition
))
```

## Multiple Possible Outcomes

When background work can end in different states:

```csharp
.Triggers(AsyncOperation.Create<JobQueueState>(
    isTerminal: s => s.Jobs[jobId] != JobStatus.Pending,
    transitions: new Action<JobQueueState>[] {
        next => next.Jobs[jobId] = JobStatus.Completed,   // Success
        next => next.Jobs[jobId] = JobStatus.Failed,      // Failure
        next => next.Jobs[jobId] = JobStatus.Cancelled    // Cancelled
    }
))
```

All three outcomes are valid until observation tells us which actually occurred.

## Complete Example: Job Queue

```csharp
[State]
public partial class JobQueueState
{
    public Dictionary<string, JobInfo> Jobs { get; set; } = new();
}

[State]
public partial class JobInfo
{
    public JobStatus Status { get; set; }
    public string? Result { get; set; }
}

public enum JobStatus { Pending, Running, Completed, Failed }

var spec = new Spec<JobQueueState>();

// Create Job - triggers background processing
spec.Operation<CreateJobRequest, ApiResult<Job>>("CreateJob", (request, state) =>
{
    if (state.Jobs.ContainsKey(request.JobId))
        return Expect.That<ApiResult<Job>>(r => r.IsConflict).SameState();

    return Expect.That<ApiResult<Job>>(r => r.IsSuccess && r.Data.Status == JobStatus.Pending)
           .ThenState<JobQueueState>(next => next.Jobs[request.JobId] = new JobInfo
           {
               Status = JobStatus.Pending
           })
           .Triggers(AsyncOperation.Create<JobQueueState>(
               isTerminal: s => s.Jobs[request.JobId].Status != JobStatus.Pending,
               transitions: new Action<JobQueueState>[] {
                   next => next.Jobs[request.JobId] = new JobInfo
                   {
                       Status = JobStatus.Completed,
                       Result = "Success"
                   },
                   next => next.Jobs[request.JobId] = new JobInfo
                   {
                       Status = JobStatus.Failed,
                       Result = "Error occurred"
                   }
               }
           ));
});

// Get Job - observes current state
spec.Operation<string, ApiResult<Job>>("GetJob", (jobId, state) =>
{
    if (!state.Jobs.TryGetValue(jobId, out var job))
        return Expect.That<ApiResult<Job>>(r => r.IsNotFound).SameState();

    return Expect.That<ApiResult<Job>>(
               r => r.IsSuccess &&
                    r.Data.Status == job.Status &&
                    r.Data.Result == job.Result)
           .SameState();
});
```

## Common Patterns

### Polling Until Specific State

```csharp
// Wait for Completed specifically
for (int i = 0; i < maxRetries; i++)
{
    var response = await client.GetJob(jobId);
    if (response.Data.Status == JobStatus.Completed)
        break;
    if (response.Data.Status == JobStatus.Failed)
        throw new Exception($"Job failed: {response.Data.Error}");
    await Task.Delay(pollInterval);
}
```

### Timeout with Validation

```csharp
var timeout = TimeSpan.FromSeconds(30);
var start = DateTime.UtcNow;

while (DateTime.UtcNow - start < timeout)
{
    var response = await client.GetJob(jobId);
    spec.Allows(getJobOp, jobId, response, stateProfile);

    if (IsTerminal(response))
        return response;

    await Task.Delay(100);
}
throw new TimeoutException("Job did not complete");
```

## Best Practices

1. **Keep isTerminal simple**: Should be a quick check on state
2. **Cover all outcomes**: Include both success and failure transitions
3. **Set reasonable poll intervals**: Not too fast (hammers API), not too slow (slow tests)
4. **Always set MaxRetryCount**: Prevents infinite loops on stuck jobs
5. **Test liveness**: Ensure background work actually completes

## Next Steps

- **Patterns**: Response-dependent state, request derivations
- **Troubleshooting**: Debug async test failures
