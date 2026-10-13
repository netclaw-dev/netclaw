---
name: accordant-overview
description: What Accordant is, when to use model-based testing, and core concepts - use this skill when starting with Accordant or explaining its purpose
---

# Accordant Overview

Accordant is a **model-based testing framework** for .NET. You write a *spec* — executable code that captures what your system should do — and Accordant generates tests, validates responses, and finds bugs.

## When to Use Accordant

Accordant excels when:
- Your system is **stateful** (databases, caches, queues, sessions)
- You have **many operations** that interact with shared state
- You want to test **operation sequences** (not just individual calls)
- You need to find **race conditions** in concurrent code
- You want a **single source of truth** for behavior (not scattered assertions)

## Core Concepts

### The Spec

A spec is executable code defining what your system should do:

```csharp
var spec = new Spec<BankState>();

spec.Operation<string, ApiResult<decimal>>("CreateAccount", (accountId, state) =>
{
    if (state.Accounts.ContainsKey(accountId))
        return Expect.That<ApiResult<decimal>>(r => r.IsConflict).SameState();

    return Expect.That<ApiResult<decimal>>(r => r.IsSuccess && r.Data == 0)
           .ThenState<BankState>(s => s.Accounts[accountId] = 0);
});
```

### State

State is what the system "remembers" between operations. Keep it minimal — only what's needed to define correct behavior:

```csharp
[State]
public partial class BankState
{
    public Dictionary<string, decimal> Accounts { get; set; } = new();
}
```

### Operations

An operation represents an atomic action — API call, method invocation, command. Each operation has:
- **Apply**: What *should* happen (the spec logic)
- **Execute**: What *actually* happens (calls the real system)

### Test Generation

Accordant explores operation sequences by simulating the spec, then runs generated tests against your real system:

```csharp
var testCases = spec.GenerateTests(initialState, inputs, options);
var results = await spec.RunTests(context, initialState, testCases, executionOptions);
```

## The Typical Workflow

1. **Define State** — What does the system track?
2. **Define Operations** — What can happen to that state?
3. **Bind to Real System** — Connect spec operations to actual API calls
4. **Generate & Run Tests** — Let Accordant explore and validate

## Example: BankAccount Spec

```csharp
[State]
public partial class BankState
{
    public Dictionary<string, decimal> Accounts { get; set; } = new();
}

var spec = new Spec<BankState>();

// Create Account
spec.Operation<string, ApiResult<decimal>>("CreateAccount", (accountId, state) =>
{
    if (state.Accounts.ContainsKey(accountId))
        return Expect.That<ApiResult<decimal>>(r => r.IsConflict).SameState();

    return Expect.That<ApiResult<decimal>>(r => r.IsSuccess && r.Data == 0)
           .ThenState<BankState>(s => s.Accounts[accountId] = 0);
});

// Withdraw
spec.Operation<(string AccountId, decimal Amount), ApiResult<decimal>>("Withdraw", (req, state) =>
{
    if (!state.Accounts.TryGetValue(req.AccountId, out var balance))
        return Expect.That<ApiResult<decimal>>(r => r.IsNotFound).SameState();

    if (balance < req.Amount)
        return Expect.That<ApiResult<decimal>>(r => r.IsBadRequest).SameState();

    var newBalance = balance - req.Amount;
    return Expect.That<ApiResult<decimal>>(r => r.IsSuccess && r.Data == newBalance)
           .ThenState<BankState>(s => s.Accounts[req.AccountId] = newBalance);
});
```

## Key Benefits

| Benefit | Description |
|---------|-------------|
| **Single source of truth** | Business rules live in one place, not scattered across tests |
| **Automatic test generation** | Cover operation sequences you'd never think to write manually |
| **Concurrency testing** | Find race conditions with linearizability checking |
| **Confidence through change** | Refactor freely; the spec validates correctness |

## Next Steps

- **State Design**: Learn to model state effectively
- **Operations**: Master the Apply/Execute pattern
- **Test Generation**: Configure and run auto-generated tests
