---
name: accordant-test-generation
description: How Accordant generates and runs tests - use this skill when configuring test generation, understanding state graphs, or running tests
---

# Test Generation in Accordant

Accordant generates tests by exploring the state graph defined by your spec, then running the generated sequences against your real system.

## The State Graph

Starting from an initial state, Accordant applies each input to compute resulting states, then recurses. This builds a graph where:
- **Nodes** = States the system can be in
- **Edges** = Operations that transition between states

## Generating Tests

### Basic Usage

```csharp
var spec = CreateSpec();
var initialState = new BankState();

// Define inputs to explore
var inputs = new InputSet
{
    spec.GetOperation<string, ApiResult<decimal>>("CreateAccount").With("alice", "Create alice"),
    spec.GetOperation<(string, decimal), ApiResult<decimal>>("Deposit").With(("alice", 100m), "Deposit 100"),
    spec.GetOperation<(string, decimal), ApiResult<decimal>>("Withdraw").With(("alice", 30m), "Withdraw 30"),
    spec.GetOperation<(string, decimal), ApiResult<decimal>>("Withdraw").With(("alice", 200m), "Withdraw 200 (overdraft)"),
    spec.GetOperation<string, ApiResult<decimal>>("DeleteAccount").With("alice", "Delete alice"),
};

// Generate test cases
var testCases = spec.GenerateTests(initialState, inputs, new TestGenerationOptions
{
    MaxDepth = 5
});
```

### TestGenerationOptions

| Option | Description | Default |
|--------|-------------|---------|
| `MaxDepth` | Maximum sequence length | 5 |
| `StateConstraint` | Prune states during exploration | None |
| `MaxConcurrencyLevel` | For concurrent test generation | 3 |
| `SequentialTestCaseAlgorithm` | How paths are extracted | StateCoverage |

### Constraining the State Space

Prevent state explosion with `StateConstraint`:

```csharp
var options = new TestGenerationOptions
{
    MaxDepth = 5,
    StateConstraint = state =>
    {
        var s = (BankState)state;
        // Limit: max 2 accounts, balances under 500
        return s.Accounts.Count <= 2 &&
               s.Accounts.Values.All(b => b <= 500);
    }
};
```

## Test Case Algorithms

### StateCoverage (Default)

Visits each unique state at least once:

```csharp
var options = new TestGenerationOptions
{
    SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.StateCoverage
};
```

### TransitionCoverage

Exercises every edge (transition) in the graph — can generate many tests:

```csharp
var options = new TestGenerationOptions
{
    SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(maxSequenceLength: 4)
};
```

### RandomWalk

Samples paths probabilistically — good for large state spaces:

```csharp
var options = new TestGenerationOptions
{
    SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateRandomWalk(
        numberOfWalks: 200,
        maxWalkLength: 8,
        seed: 42)  // For reproducibility
};
```

## Running Tests

### Basic Execution

```csharp
var context = spec.CreateTestingContext();

// Register your test client
var client = new BankApiClient(httpClient);
context.Register(client);

var results = await spec.RunTests(context, initialState, testCases, new TestExecutionOptions
{
    BeforeEachAsync = async _ =>
    {
        // Reset system before each test
        await client.DeleteAccount("alice");
        await client.DeleteAccount("bob");
    }
});

// Check results
var failures = results.Where(r => !r.Success).ToList();
Assert.IsEmpty(failures, $"Failed: {failures.FirstOrDefault()?.LastFailureMessage}");
```

### TestExecutionOptions

| Option | Description |
|--------|-------------|
| `BeforeEachAsync` | Reset system before each test case |
| `AfterEachAsync` | Cleanup after each test case |
| `OnStepExecuted` | Log each operation's request/response for debugging |

## Resetting State

Each test expects the system to start in the initial state. Reset with `BeforeEachAsync`:

```csharp
var knownAccounts = new[] { "alice", "bob" };

var results = await spec.RunTests(context, initialState, testCases, new TestExecutionOptions
{
    BeforeEachAsync = async _ =>
    {
        foreach (var accountId in knownAccounts)
        {
            await client.DeleteAccount(accountId);  // Ignore 404
        }
    }
});
```

## Visualizing the State Graph

Generate a GraphViz visualization:

```csharp
var dot = spec.VisualizeStateSpace(initialState, inputs, new TestGenerationOptions { MaxDepth = 4 });
File.WriteAllText("state-graph.dot", dot);

// Convert to PNG: dot -Tpng state-graph.dot -o state-graph.png
```

### Custom Node Labels

```csharp
var dot = spec.VisualizeStateSpace(
    initialState,
    inputs,
    new TestGenerationOptions { MaxDepth = 3 },
    new VisualizationOptions
    {
        NodeLabelLambda = node =>
        {
            var state = (BankState)node.State;
            return $"Accounts: {state.Accounts.Count}\\nTotal: {state.Accounts.Values.Sum()}";
        }
    });
```

## Manual Conformance Testing

Validate individual operations without auto-generation:

```csharp
var stateProfile = new StateProfile(new BankState());

// Execute and validate
var response = await client.CreateAccount("alice");
(bool isValid, string message, stateProfile) =
    spec.Allows(spec.GetOperation("CreateAccount"), "alice", response, stateProfile);

Assert.IsTrue(isValid, message);

// Continue with more operations...
var withdrawResponse = await client.Withdraw("alice", 50);
(isValid, message, stateProfile) =
    spec.Allows(spec.GetOperation("Withdraw"), ("alice", 50m), withdrawResponse, stateProfile);

Assert.IsTrue(isValid, message);
```

## Complete Example

```csharp
[Test]
public async Task AutoGeneratedTests()
{
    var spec = CreateSpec();

    // Bind to real API
    spec.ExecuteWith<BankApiClient>()
        .Bind<string, ApiResult<decimal>>("CreateAccount", (c, id) => c.CreateAccount(id).Result)
        .Bind<(string, decimal), ApiResult<decimal>>("Deposit", (c, r) => c.Deposit(r.Item1, r.Item2).Result)
        .Bind<(string, decimal), ApiResult<decimal>>("Withdraw", (c, r) => c.Withdraw(r.Item1, r.Item2).Result)
        .Bind<string, ApiResult<decimal>>("DeleteAccount", (c, id) => c.DeleteAccount(id).Result);

    var inputs = new InputSet
    {
        spec.GetOperation<string, ApiResult<decimal>>("CreateAccount").With("alice", "Create alice"),
        spec.GetOperation<(string, decimal), ApiResult<decimal>>("Deposit").With(("alice", 100m), "Deposit 100"),
        spec.GetOperation<(string, decimal), ApiResult<decimal>>("Withdraw").With(("alice", 30m), "Withdraw 30"),
    };

    var testCases = spec.GenerateTests(new BankState(), inputs, new TestGenerationOptions { MaxDepth = 4 });

    var context = spec.CreateTestingContext();
    context.Register(new BankApiClient(CreateHttpClient()));

    var results = await spec.RunTests(context, new BankState(), testCases, new TestExecutionOptions
    {
        BeforeEachAsync = async _ => await ResetDatabase()
    });

    Assert.That(results.All(r => r.Success), $"Failed: {results.FirstOrDefault(r => !r.Success)?.LastFailureMessage}");
}
```

## Common Issues

### Too Many Test Cases

- Reduce `MaxDepth`
- Add `StateConstraint` to prune uninteresting states
- Use fewer inputs or more targeted inputs

### Tests Failing Unpredictably

- Ensure `BeforeEachAsync` properly resets state
- Check for timing issues (async operations may not complete)
- Verify inputs match what the spec expects

## Next Steps

- **Concurrency Testing**: Test race conditions with `GenerateConcurrentTests` and `RunTests`
- **Async Operations**: Handle background work with polling
