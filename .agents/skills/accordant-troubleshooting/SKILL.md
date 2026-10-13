---
name: accordant-troubleshooting
description: Common mistakes and debugging tips for Accordant specs - use this skill when tests fail unexpectedly or specs don't behave as expected
---

# Troubleshooting Accordant

This skill covers common mistakes, debugging techniques, and how to fix typical issues.

## Test Failures

### "No matching expected outcome"

The response doesn't match any `Expect.That()` predicate.

**Diagnosis:**
```csharp
// Check what the spec expects vs what you got
var (isValid, message, _) = spec.Allows(operation, request, actualResponse, stateProfile);
Console.WriteLine(message);  // Shows which expectation failed and why
```

**Common causes:**
1. **Predicate too strict**: Response has extra fields or slightly different values
2. **Wrong state**: The spec thinks it's in state A, but the system is in state B
3. **Missing error case**: You didn't handle an error condition in Apply

**Fix:**
```csharp
// Add explanation to predicates for better debugging
Expect.That<ApiResult<User>>(
    r => r.IsSuccess && r.Data.Name == expectedName,
    $"Expected success with name '{expectedName}', got status={r?.StatusCode}, name='{r?.Data?.Name}'")
```

### "State mismatch after operation"

Tracked state diverges from actual system state.

**Common causes:**
1. **Forgot to reset state**: Previous test left data behind
2. **Missing ThenState**: Operation modifies state but spec uses `.SameState()`
3. **Wrong state update**: ThenState modifies wrong fields

**Fix:**
```csharp
// Ensure BeforeEachAsync properly resets
var results = await spec.RunTests(context, initialState, testCases, new TestExecutionOptions
{
    BeforeEachAsync = async _ =>
    {
        // Actually verify reset worked
        await client.DeleteAll();
        var count = await client.GetCount();
        if (count != 0) throw new Exception("Reset failed!");
    }
});
```

### Tests Pass Individually, Fail in Sequence

**Common causes:**
1. **Shared mutable state** in test fixtures
2. **Incomplete reset** between tests
3. **Timing issues** with async operations

**Fix:**
```csharp
// Create fresh client for each test
var results = await spec.RunTests(context, initialState, testCases, new TestExecutionOptions
{
    BeforeEachAsync = async ctx =>
    {
        // Fresh client, fresh start
        var httpClient = _factory.CreateClient();
        ctx.Context.Register(new ApiClient(httpClient));
        await ResetDatabase();
    }
});
```

## State Issues

### Modifying Original State

```csharp
// ❌ WRONG - modifying original state
spec.Operation<string, ApiResult<decimal>>("Deposit", (accountId, state) =>
{
    state.Accounts[accountId] += 100;  // BAD! Modifies original
    return Expect.That(...).SameState();  // State already corrupted
});

// ✅ CORRECT - modify the clone in ThenState
spec.Operation<string, ApiResult<decimal>>("Deposit", (accountId, state) =>
{
    var newBalance = state.Accounts[accountId] + 100;
    return Expect.That<ApiResult<decimal>>(r => r.Data == newBalance)
           .ThenState<BankState>(nextState => nextState.Accounts[accountId] = newBalance);
});
```

### State Class Missing [State] Attribute

```csharp
// ❌ Won't work - no cloning/equality support
public class BankState
{
    public Dictionary<string, decimal> Accounts { get; set; }
}

// ✅ Add [State] attribute
[State]
public partial class BankState
{
    public Dictionary<string, decimal> Accounts { get; set; } = new();
}
```

### Nested State Not Marked

```csharp
// ❌ UserState isn't cloned properly
[State]
public partial class AppState
{
    public Dictionary<string, UserState> Users { get; set; }  // UserState needs [State] too!
}

public class UserState { ... }  // Missing [State]

// ✅ Mark all nested state classes
[State]
public partial class AppState
{
    public Dictionary<string, UserState> Users { get; set; } = new();
}

[State]
public partial class UserState
{
    public string Name { get; set; } = string.Empty;
}
```

## Execution Issues

### Operations Not Bound

```
System.InvalidOperationException: No execution binding found for operation 'CreateAccount'
```

**Fix:**
```csharp
spec.ExecuteWith<ApiClient>()
    .Bind<string, ApiResult<decimal>>("CreateAccount",  // Must match operation name exactly
        (client, accountId) => client.CreateAccount(accountId).Result);
```

### Type Mismatch in Binding

```csharp
// ❌ Wrong - request type doesn't match
spec.Operation<(string, decimal), ApiResult<decimal>>("Withdraw", ...);  // Tuple
spec.ExecuteWith<ApiClient>()
    .Bind<string, ApiResult<decimal>>("Withdraw", ...);  // String - MISMATCH!

// ✅ Types must match exactly
spec.ExecuteWith<ApiClient>()
    .Bind<(string, decimal), ApiResult<decimal>>("Withdraw",
        (client, req) => client.Withdraw(req.Item1, req.Item2).Result);
```

### Async/Await Issues

```csharp
// ❌ Potential deadlock with .Result
spec.ExecuteWith<ApiClient>()
    .Bind<string, ApiResult<User>>("GetUser",
        (client, id) => client.GetUserAsync(id).Result);  // Can deadlock!

// ✅ Use async binding
spec.ExecuteWith<ApiClient>()
    .BindAsync<string, ApiResult<User>>("GetUser",
        async (client, id) => await client.GetUserAsync(id));
```

## Test Generation Issues

### Too Many Test Cases

**Diagnosis:** Generation produces thousands of tests

**Fixes:**
```csharp
var options = new TestGenerationOptions
{
    // Reduce depth
    MaxDepth = 3,  // Instead of 5

    // Add state constraint
    StateConstraint = state =>
    {
        var s = (AppState)state;
        return s.Users.Count <= 2 &&
               s.Users.Values.Sum(u => u.Todos.Count) <= 4;
    }
};
```

### No Test Cases Generated

**Diagnosis:** `testCases.Count == 0`

**Common causes:**
1. **Initial state doesn't allow any operation**
2. **StateConstraint rejects initial state**
3. **Empty InputSet**

**Fix:**
```csharp
// Verify initial state is valid
var initialState = new AppState();
Assert.True(options.StateConstraint?.Invoke(initialState) ?? true, "Initial state rejected!");

// Ensure inputs aren't empty
Assert.That(inputs.Count, Is.GreaterThan(0));
```

### State Space Explosion

**Symptoms:** Generation hangs or runs out of memory

**Fix:**
```csharp
var options = new TestGenerationOptions
{
    MaxDepth = 4,
    StateConstraint = state =>
    {
        var s = (AppState)state;
        // Aggressive pruning
        return s.Items.Count <= 3;
    },
    // Or use random sampling
    SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateRandomWalk(
        numberOfWalks: 100,
        maxWalkLength: 5,
        seed: 42)
};
```

## Concurrency Test Issues

### All Concurrent Tests Fail

**Common cause:** Implementation has no concurrency control — race conditions everywhere

**Diagnosis:** Check if sequential tests pass first
```csharp
// Run sequential tests first
var seqResults = await spec.RunTests(context, initialState, testCases);
Assert.That(seqResults.All(r => r.Success), "Fix sequential tests first!");

// Then run concurrent
var concTestCases = spec.GenerateConcurrentTests(initialState, inputs, options);
var concResults = await spec.RunTests(context, initialState, concTestCases);
```

### Non-Deterministic Failures

**Symptoms:** Same test sometimes passes, sometimes fails

**Common causes:**
1. **Race conditions** (the bug you're looking for!)
2. **Incomplete state reset**
3. **External system interference**

**Diagnosis:**
```csharp
// Run same test multiple times
for (int i = 0; i < 10; i++)
{
    await ResetState();
    var concTestCases = spec.GenerateConcurrentTests(initialState, inputs, options);
    var result = await spec.RunTests(context, initialState, concTestCases);
    Console.WriteLine($"Run {i}: {(result.All(r => r.Success) ? "PASS" : "FAIL")}");
}
```

## Async Operation Issues

### Polling Never Terminates

**Symptoms:** Test hangs on async operation

**Diagnosis:**
```csharp
// Check isTerminal predicate
var state = GetCurrentState();
var isTerminal = stepFunction.IsTerminal(state);
Console.WriteLine($"IsTerminal: {isTerminal}, State: {state}");
```

**Common causes:**
1. **isTerminal predicate is wrong** — never returns true
2. **Background work never completes** — liveness bug
3. **Wrong state being checked**

**Fix:**
```csharp
.Triggers(AsyncOperation.Create<JobQueueState>(
    // Make sure this actually becomes true
    isTerminal: s =>
    {
        var status = s.Jobs.GetValueOrDefault(jobId)?.Status;
        Console.WriteLine($"Checking terminal: status={status}");
        return status != JobStatus.Pending;
    },
    ...
))
```

### State Profile Has Too Many States

**Symptoms:** After async operation, many possible states tracked

**This is expected!** Non-determinism means multiple outcomes are valid. Observations narrow it down.

```csharp
// Observe to narrow down
var response = await client.GetJob(jobId);
(_, _, stateProfile) = spec.Allows(getJobOp, jobId, response, stateProfile);

// State count should decrease
Console.WriteLine($"Remaining possible states: {stateProfile.StatesAndStepFunctions.Count}");
```

## Debugging Tips

### Enable Detailed Logging

```csharp
var results = await spec.RunTests(context, initialState, testCases, new TestExecutionOptions
{
    OnStepExecuted = info =>
    {
        foreach (var (op, req, resp) in info.Operations)
            Console.WriteLine($"{op.Name}({req}) → {resp}");
    }
});
```

### Visualize the State Graph

```csharp
var dot = spec.VisualizeStateSpace(initialState, inputs, options);
File.WriteAllText("debug-graph.dot", dot);
// Run: dot -Tpng debug-graph.dot -o debug-graph.png
```

### Inspect a Single Test Case

```csharp
var failedCase = testCases.First(tc => !results[tc].Success);
Console.WriteLine($"Failed sequence:");
foreach (var step in failedCase.Steps)
{
    Console.WriteLine($"  {step.OperationName}({step.Request})");
}
```

### Manually Replay a Sequence

```csharp
var stateProfile = new StateProfile(initialState);

foreach (var step in failedCase.Steps)
{
    Console.WriteLine($"\n=== {step.OperationName} ===");
    Console.WriteLine($"Request: {step.Request}");
    Console.WriteLine($"Current states: {stateProfile.StatesAndStepFunctions.Count}");

    var response = await ExecuteStep(step);
    Console.WriteLine($"Response: {response}");

    var (isValid, message, newProfile) = spec.Allows(step.Operation, step.Request, response, stateProfile);
    Console.WriteLine($"Valid: {isValid}");
    if (!isValid) Console.WriteLine($"Error: {message}");

    stateProfile = newProfile;
}
```

## Getting Help

1. **Check the samples**: `Samples/` folder has working examples
2. **Read the concepts docs**: `docs/concepts/` explains the theory
3. **Visualize**: State graphs often reveal the problem
4. **Simplify**: Reduce to minimal failing case
