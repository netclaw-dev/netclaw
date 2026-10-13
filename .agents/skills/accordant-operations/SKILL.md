---
name: accordant-operations
description: How to define operations with Apply and Execute methods - use this skill when creating spec operations or understanding the Apply/Execute pattern
---

# Operations in Accordant

An **operation** represents an atomic action your system can perform. Each operation has two parts:
- **Apply**: Describes what *should* happen (pure spec logic)
- **Execute**: Makes it *actually* happen (calls the real system)

## Defining Operations

### Inline Syntax

For simple specs, define operations inline:

```csharp
var spec = new Spec<BankState>();

spec.Operation<string, ApiResult<decimal>>("CreateAccount", (accountId, state) =>
{
    // Apply logic here
    if (state.Accounts.ContainsKey(accountId))
        return Expect.That<ApiResult<decimal>>(r => r.IsConflict).SameState();

    return Expect.That<ApiResult<decimal>>(r => r.IsSuccess && r.Data == 0)
           .ThenState<BankState>(s => s.Accounts[accountId] = 0);
});
```

### Binding Execution

Connect spec operations to real system calls:

```csharp
spec.ExecuteWith<BankApiClient>()
    .Bind<string, ApiResult<decimal>>("CreateAccount",
        (client, accountId) => client.CreateAccount(accountId).Result)
    .Bind<(string, decimal), ApiResult<decimal>>("Withdraw",
        (client, req) => client.Withdraw(req.Item1, req.Item2).Result);
```

### Class-Based Operations

For complex operations, use classes:

```csharp
public class WithdrawOperation : Operation<WithdrawRequest, ApiResult<decimal>, BankState>
{
    public override ExpectedOutcomes Apply(WithdrawRequest request, BankState state)
    {
        if (!state.Accounts.TryGetValue(request.AccountId, out var balance))
            return Expect.That<ApiResult<decimal>>(r => r.IsNotFound).SameState();

        if (balance < request.Amount)
            return Expect.That<ApiResult<decimal>>(r => r.IsBadRequest).SameState();

        var newBalance = balance - request.Amount;
        return Expect.That<ApiResult<decimal>>(r => r.IsSuccess && r.Data == newBalance)
               .ThenState<BankState>(s => s.Accounts[request.AccountId] = newBalance);
    }

    public override async Task<ApiResult<decimal>> ExecuteAsync(
        WithdrawRequest request,
        TestingContext context)
    {
        var client = context.Get<BankApiClient>();
        return await client.WithdrawAsync(request.AccountId, request.Amount);
    }
}
```

## The Apply Method

Apply defines expected behavior. Given a request and state, return `ExpectedOutcomes`:

```csharp
spec.Operation<(string AccountId, decimal Amount), ApiResult<decimal>>("Deposit", (request, state) =>
{
    // Check preconditions first (guard clauses)
    if (!state.Accounts.TryGetValue(request.AccountId, out var balance))
        return Expect.That<ApiResult<decimal>>(r => r.IsNotFound).SameState();

    // Happy path last
    var newBalance = balance + request.Amount;
    return Expect.That<ApiResult<decimal>>(r => r.IsSuccess && r.Data == newBalance)
           .ThenState<BankState>(s => s.Accounts[request.AccountId] = newBalance);
});
```

**Key points:**
- Apply is **pure** — no side effects, no API calls
- Check error conditions first with early returns
- Return expected response + state transition

## Expect.That — Validating Responses

### Basic Predicate

```csharp
Expect.That<int>(r => r == 42)
```

### With Explanation (Recommended)

```csharp
Expect.That<ApiResult<decimal>>(
    r => r.IsSuccess && r.Data == expectedBalance,
    $"Should return 200 OK with balance {expectedBalance}")
```

Explanations appear in test failure output — crucial for debugging.

### With ValidationResult

For complex validations with detailed error messages:

```csharp
Expect.That<User>(response =>
{
    if (response.Name != expectedName)
        return ValidationResult.Invalid($"Name was '{response.Name}', expected '{expectedName}'");
    if (response.Email == null)
        return ValidationResult.Invalid("Email was null");
    return ValidationResult.Valid();
})
```

## State Transitions

### No Change: `.SameState()`

```csharp
// Error cases
return Expect.That<ApiResult<User>>(r => r.IsNotFound).SameState();

// Read-only operations
return Expect.That<ApiResult<User>>(r => r.IsSuccess).SameState();
```

### State Change: `.ThenState()`

```csharp
return Expect.That<ApiResult<decimal>>(r => r.IsSuccess && r.Data == newBalance)
       .ThenState<BankState>(nextState => nextState.Accounts[accountId] = newBalance);
```

### Response-Dependent State

When state depends on server-generated values:

```csharp
return Expect.That<ApiResult<Order>>(r => r.IsSuccess && !string.IsNullOrEmpty(r.Data.OrderId))
       .ThenState<AppState>(
           (ApiResult<Order> response, AppState nextState) =>
               nextState.Orders[response.Data.OrderId] = new OrderState { /* ... */ },
           mock: () => new ApiResult<Order>
           {
               Data = new Order { OrderId = Guid.NewGuid().ToString() },
               StatusCode = 201
           });
```

## Multiple Valid Outcomes: Expect.OneOf

When more than one outcome is valid (timeouts, non-determinism):

```csharp
spec.Operation<string, ApiResult<decimal>>("CreateAccount", (accountId, state) =>
{
    if (state.Accounts.ContainsKey(accountId))
        return Expect.That<ApiResult<decimal>>(r => r.IsConflict).SameState();

    return Expect.OneOf(
        // Success
        Expect.That<ApiResult<decimal>>(r => r.IsSuccess && r.Data == 0)
              .ThenState<BankState>(s => s.Accounts[accountId] = 0),

        // Timeout - request lost
        Expect.That<ApiResult<decimal>>(r => r.IsTimeout)
              .SameState(),

        // Timeout - response lost (account was created)
        Expect.That<ApiResult<decimal>>(r => r.IsTimeout)
              .ThenState<BankState>(s => s.Accounts[accountId] = 0)
    );
});
```

## Exception Expectations

When operations should throw:

```csharp
spec.Operation<string, Unit>("DeleteUser", (userId, state) =>
{
    if (!state.Users.ContainsKey(userId))
        return Expect.Throws<NotFoundException>("User not found").SameState();

    if (state.Users[userId].HasPendingOrders)
        return Expect.Throws<BusinessRuleException>("Cannot delete user with pending orders").SameState();

    return Expect.That<Unit>(r => true)
           .ThenState<AppState>(s => s.Users.Remove(userId));
});
```

Use `Unit` as the response type for void operations.

## Writing Strong Predicates

**Weak** (catches fewer bugs):
```csharp
Expect.That<ApiResult<User>>(r => r.IsSuccess)  // Only checks status
```

**Strong** (catches more bugs):
```csharp
Expect.That<ApiResult<User>>(
    r => r.IsSuccess &&
         r.Data.Id == expectedId &&
         r.Data.Name == expectedName &&
         r.Data.Email == expectedEmail)
```

The stronger your predicates, the more bugs you'll catch.

## Common Patterns

### The Guard-Clause Pattern

```csharp
spec.Operation<TransferRequest, ApiResult<decimal>>("Transfer", (req, state) =>
{
    // Guards first
    if (!state.Accounts.ContainsKey(req.FromAccount))
        return Expect.That<ApiResult<decimal>>(r => r.IsNotFound, "Source not found").SameState();

    if (!state.Accounts.ContainsKey(req.ToAccount))
        return Expect.That<ApiResult<decimal>>(r => r.IsNotFound, "Target not found").SameState();

    var balance = state.Accounts[req.FromAccount];
    if (balance < req.Amount)
        return Expect.That<ApiResult<decimal>>(r => r.IsBadRequest, "Insufficient funds").SameState();

    // Happy path last
    var newFromBalance = balance - req.Amount;
    var newToBalance = state.Accounts[req.ToAccount] + req.Amount;

    return Expect.That<ApiResult<decimal>>(r => r.IsSuccess && r.Data == newFromBalance)
           .ThenState<BankState>(s => {
               s.Accounts[req.FromAccount] = newFromBalance;
               s.Accounts[req.ToAccount] = newToBalance;
           });
});
```

## Next Steps

- **Test Generation**: See how operations combine into test sequences
- **Async Operations**: Model background work with step functions
