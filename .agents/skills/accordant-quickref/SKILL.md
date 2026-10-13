---
name: accordant-quickref
description: Quick reference and cheatsheet for Accordant API - use this skill for quick syntax lookup or code snippets
---

# Accordant Quick Reference

## State Definition

```csharp
[State]
public partial class MyState
{
    public Dictionary<string, decimal> Balances { get; set; } = new();
    public Dictionary<string, UserState> Users { get; set; } = new();
}

[State]
public partial class UserState
{
    public string Name { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
}
```

## Create a Spec

```csharp
var spec = new Spec<MyState>()
    .WithJsonPrinters();  // Optional: nice logging
```

## Define Operations

### Inline Syntax

```csharp
spec.Operation<TRequest, TResponse>("OperationName", (request, state) =>
{
    // Return ExpectedOutcomes
});
```

### Examples

```csharp
// Simple request (single value)
spec.Operation<string, ApiResult<User>>("GetUser", (userId, state) => { ... });

// Tuple request (multiple values)
spec.Operation<(string Id, decimal Amount), ApiResult<decimal>>("Deposit", (request, state) =>
{
    var (id, amount) = request;  // Destructure
    // ...
});

// Class-based request
spec.Operation<CreateUserRequest, ApiResult<User>>("CreateUser", (request, state) => { ... });
```

## Expected Outcomes

### Basic Expectation

```csharp
Expect.That<TResponse>(response => /* predicate */)
```

### With Explanation (Recommended)

```csharp
Expect.That<ApiResult<decimal>>(
    r => r.IsSuccess && r.Data == expectedValue,
    $"Expected success with value {expectedValue}")
```

### With ValidationResult

```csharp
Expect.That<User>(response =>
{
    if (response.Name != expected)
        return ValidationResult.Invalid($"Name mismatch: {response.Name}");
    return ValidationResult.Valid();
})
```

### Exception Expectation

```csharp
Expect.Throws<NotFoundException>("Resource not found")
```

### Multiple Valid Outcomes

```csharp
Expect.OneOf(
    Expect.That<T>(r => /* outcome 1 */).ThenState(...),
    Expect.That<T>(r => /* outcome 2 */).SameState(),
    Expect.That<T>(r => /* outcome 3 */).ThenState(...)
)
```

## State Transitions

### No Change

```csharp
.SameState()
```

### State Changes

```csharp
.ThenState<MyState>(nextState => nextState.Items[id] = newValue)
```

### Response-Dependent State

```csharp
.ThenState<MyState>(
    (TResponse response, MyState nextState) =>
        nextState.Items[response.Id] = new Item { ... },
    mock: () => new TResponse { Id = Guid.NewGuid().ToString() })
```

## Async Operations

```csharp
.ThenState<MyState>(next => next.Jobs[id] = JobStatus.Pending)
.Triggers(AsyncOperation.Create<MyState>(
    isTerminal: s => s.Jobs[id] != JobStatus.Pending,
    transitions: new Action<MyState>[] {
        next => next.Jobs[id] = JobStatus.Completed,
        next => next.Jobs[id] = JobStatus.Failed
    }
))
```

## Bind Execution

```csharp
spec.ExecuteWith<ApiClient>()
    .Bind<TRequest, TResponse>("OperationName",
        (client, request) => client.Method(request).Result)
    .BindAsync<TRequest, TResponse>("AsyncOperation",
        async (client, request) => await client.MethodAsync(request));
```

## Test Generation

### Define Inputs

```csharp
var inputs = new InputSet
{
    spec.GetOperation<string, ApiResult<User>>("CreateUser").With("alice", "Create Alice"),
    spec.GetOperation<(string, decimal), ApiResult<decimal>>("Deposit").With(("alice", 100m), "Deposit 100"),
};
```

### Generate Tests

```csharp
var testCases = spec.GenerateTests(initialState, inputs, new TestGenerationOptions
{
    MaxDepth = 5,
    StateConstraint = state => ((MyState)state).Items.Count <= 3
});
```

### Run Tests

```csharp
var context = spec.CreateTestingContext();
context.Register(new ApiClient(httpClient));

var results = await spec.RunTests(context, initialState, testCases, new TestExecutionOptions
{
    BeforeEachAsync = async _ => await ResetDatabase()
});

Assert.That(results.All(r => r.Success));
```

## Concurrent Tests

```csharp
var concurrentTestCases = spec.GenerateConcurrentTests(initialState, inputs,
    new TestGenerationOptions { MaxDepth = 4 });

var results = await spec.RunTests(context, initialState, concurrentTestCases,
    new TestExecutionOptions { ... });
```

## Manual Conformance Testing

```csharp
var stateProfile = new StateProfile(initialState);

var response = await client.CreateUser("alice");
(bool isValid, string message, stateProfile) =
    spec.Allows(createUserOp, "alice", response, stateProfile);

Assert.IsTrue(isValid, message);
```

## Visualization

```csharp
var dot = spec.VisualizeStateSpace(initialState, inputs, options);
File.WriteAllText("graph.dot", dot);
// Convert: dot -Tpng graph.dot -o graph.png
```

## TestGenerationOptions

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `MaxDepth` | int | 5 | Max sequence length |
| `StateConstraint` | Func<State, bool> | null | Prune states |
| `MaxConcurrencyLevel` | int | 3 | Concurrent ops |
| `SequentialTestCaseAlgorithm` | delegate | StateCoverage | Walk algorithm |

## Test Case Algorithms

```csharp
// State coverage (default)
SequentialTestCaseAlgorithms.StateCoverage

// Transition coverage
SequentialTestCaseAlgorithms.CreateTransitionCoverage(maxSequenceLength: 4)

// Random walk
SequentialTestCaseAlgorithms.CreateRandomWalk(numberOfWalks: 100, maxWalkLength: 8, seed: 42)
```

## Common Operation Pattern

```csharp
spec.Operation<(string Id, decimal Amount), ApiResult<decimal>>("Withdraw", (request, state) =>
{
    var (accountId, amount) = request;

    // Guard 1: Resource exists?
    if (!state.Accounts.TryGetValue(accountId, out var balance))
        return Expect.That<ApiResult<decimal>>(r => r.IsNotFound, "Account not found")
               .SameState();

    // Guard 2: Business rule
    if (balance < amount)
        return Expect.That<ApiResult<decimal>>(r => r.IsBadRequest, "Insufficient funds")
               .SameState();

    // Success path
    var newBalance = balance - amount;
    return Expect.That<ApiResult<decimal>>(
               r => r.IsSuccess && r.Data == newBalance,
               $"Expected new balance {newBalance}")
           .ThenState<BankState>(next => next.Accounts[accountId] = newBalance);
});
```

## Namespace

```csharp
using Microsoft.Accordant;
```

## NuGet Package

```bash
dotnet add package Microsoft.Accordant
```
