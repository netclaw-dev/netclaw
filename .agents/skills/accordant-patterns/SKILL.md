---
name: accordant-patterns
description: Common patterns in Accordant - response-dependent state, request derivations, error handling, and HTTP integration
---

# Common Patterns in Accordant

This skill covers frequently-used patterns when building Accordant specs.

## Response-Dependent State

When state depends on server-generated values (IDs, timestamps, ETags):

```csharp
spec.Operation<CreateOrderRequest, ApiResult<Order>>("CreateOrder", (request, state) =>
{
    return Expect.That<ApiResult<Order>>(
               r => r.IsSuccess && !string.IsNullOrEmpty(r.Data.OrderId))
           .ThenState<AppState>(
               // Lambda receives response AND a pre-cloned nextState
               (ApiResult<Order> response, AppState nextState) =>
                   nextState.Orders[response.Data.OrderId] = new OrderState
                   {
                       Product = request.Product,
                       Status = OrderStatus.Created
                   },
               // Mock for test generation (no real system running)
               mock: () => new ApiResult<Order>
               {
                   Data = new Order
                   {
                       OrderId = Guid.NewGuid().ToString(),
                       Product = request.Product
                   },
                   StatusCode = 201
               });
});
```

### Why the Mock?

During **test generation**, there's no real server. The mock provides a plausible response so Accordant can explore the state space. At **test execution**, the mock is ignored — real responses are used.

### Validating Captured Values

```csharp
spec.Operation<string, ApiResult<Order>>("GetOrder", (orderId, state) =>
{
    if (!state.Orders.TryGetValue(orderId, out var order))
        return Expect.That<ApiResult<Order>>(r => r.IsNotFound).SameState();

    // Validate against captured state
    return Expect.That<ApiResult<Order>>(
               r => r.IsSuccess &&
                    r.Data.OrderId == orderId &&
                    r.Data.Product == order.Product &&
                    r.Data.Status == order.Status)
           .SameState();
});
```

## Request Derivations

When one operation's request needs data from another operation's response:

```csharp
// CreateTodo returns a server-generated TodoId
// GetTodo needs that TodoId

spec.ConfigureDerivations("GetTodo",
    Derive.From<CreateTodoRequest, CreateTodoResponse, string>("CreateTodo")
        .When((createReq, createResp) => createResp.IsSuccess)
        .As((createReq, createResp) => createResp.TodoId));
```

### When You DON'T Need Derivations

If the client controls all IDs (not server-generated), no derivation needed:

```csharp
// Client picks the key
await api.CreateItem("my-key", "my-value");
await api.GetItem("my-key");  // No derivation needed - we know the key
```

Derivations are only needed when you must extract values from responses.

## HTTP API Integration

### ApiResult<T> Pattern

A common pattern for HTTP responses:

```csharp
public class ApiResult<T>
{
    public T? Data { get; set; }
    public int StatusCode { get; set; }

    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
    public bool IsNotFound => StatusCode == 404;
    public bool IsConflict => StatusCode == 409;
    public bool IsBadRequest => StatusCode == 400;
}
```

### Binding HTTP Calls

```csharp
spec.ExecuteWith<HttpClient>()
    .BindAsync<string, ApiResult<User>>("GetUser", async (client, userId) =>
    {
        var response = await client.GetAsync($"/api/users/{userId}");
        return new ApiResult<User>
        {
            StatusCode = (int)response.StatusCode,
            Data = response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<User>()
                : null
        };
    });
```

### Using a Typed Client

```csharp
public class BankApiClient
{
    private readonly HttpClient _client;

    public async Task<ApiResult<decimal>> CreateAccount(string accountId)
    {
        var response = await _client.PutAsync($"/accounts/{accountId}", null);
        return await ParseResponse<decimal>(response);
    }

    public async Task<ApiResult<decimal>> Withdraw(string accountId, decimal amount)
    {
        var content = JsonContent.Create(new { amount });
        var response = await _client.PostAsync($"/accounts/{accountId}/withdraw", content);
        return await ParseResponse<decimal>(response);
    }
}

// Bind
spec.ExecuteWith<BankApiClient>()
    .Bind<string, ApiResult<decimal>>("CreateAccount", (c, id) => c.CreateAccount(id).Result)
    .Bind<(string, decimal), ApiResult<decimal>>("Withdraw", (c, r) => c.Withdraw(r.Item1, r.Item2).Result);
```

## Error Handling Patterns

### Status Code Errors

```csharp
spec.Operation<string, ApiResult<User>>("GetUser", (userId, state) =>
{
    if (!state.Users.ContainsKey(userId))
        return Expect.That<ApiResult<User>>(r => r.IsNotFound, "User not found").SameState();

    var user = state.Users[userId];
    return Expect.That<ApiResult<User>>(r => r.IsSuccess && r.Data.Name == user.Name).SameState();
});
```

### Exception Throwing

```csharp
spec.Operation<string, Unit>("DeleteUser", (userId, state) =>
{
    if (!state.Users.ContainsKey(userId))
        return Expect.Throws<NotFoundException>("User not found").SameState();

    return Expect.That<Unit>(r => true)
           .ThenState<AppState>(s => s.Users.Remove(userId));
});
```

### Multiple Error Conditions

```csharp
spec.Operation<TransferRequest, ApiResult<decimal>>("Transfer", (req, state) =>
{
    // Check each error condition
    if (!state.Accounts.ContainsKey(req.FromAccount))
        return Expect.That<ApiResult<decimal>>(r => r.IsNotFound, "Source not found").SameState();

    if (!state.Accounts.ContainsKey(req.ToAccount))
        return Expect.That<ApiResult<decimal>>(r => r.IsNotFound, "Target not found").SameState();

    if (state.Accounts[req.FromAccount] < req.Amount)
        return Expect.That<ApiResult<decimal>>(r => r.IsBadRequest, "Insufficient funds").SameState();

    // Success path
    var newBalance = state.Accounts[req.FromAccount] - req.Amount;
    return Expect.That<ApiResult<decimal>>(r => r.IsSuccess && r.Data == newBalance)
           .ThenState<AppState>(s => {
               s.Accounts[req.FromAccount] = newBalance;
               s.Accounts[req.ToAccount] += req.Amount;
           });
});
```

## Indefinite Failures (Network Uncertainty)

When you can't tell if an operation succeeded (timeout, network error):

```csharp
spec.Operation<string, ApiResult<decimal>>("CreateAccount", (accountId, state) =>
{
    if (state.Accounts.ContainsKey(accountId))
        return Expect.That<ApiResult<decimal>>(r => r.IsConflict).SameState();

    return Expect.OneOf(
        // Success
        Expect.That<ApiResult<decimal>>(r => r.IsSuccess && r.Data == 0)
              .ThenState<BankState>(s => s.Accounts[accountId] = 0),

        // Timeout - request never reached server
        Expect.That<ApiResult<decimal>>(r => r.IsTimeout)
              .SameState(),

        // Timeout - server processed it, response lost
        Expect.That<ApiResult<decimal>>(r => r.IsTimeout)
              .ThenState<BankState>(s => s.Accounts[accountId] = 0)
    );
});
```

Both timeout outcomes are valid — you don't know which actually happened until you check.

## Validation Helpers

### Using FluentAssertions

```csharp
Expect.That<User>(response =>
{
    try
    {
        response.Should().BeEquivalentTo(expectedUser, options => options
            .Excluding(x => x.CreatedAt)
            .Excluding(x => x.UpdatedAt));
        return ValidationResult.Valid();
    }
    catch (Exception ex)
    {
        return ValidationResult.Invalid(ex.Message);
    }
})
```

### Custom Validation Logic

```csharp
Expect.That<OrderResponse>(response =>
{
    var errors = new List<string>();

    if (response.OrderId == null)
        errors.Add("OrderId was null");
    if (response.Items.Count != expectedCount)
        errors.Add($"Expected {expectedCount} items, got {response.Items.Count}");
    if (response.Total != expectedTotal)
        errors.Add($"Expected total {expectedTotal}, got {response.Total}");

    return errors.Count == 0
        ? ValidationResult.Valid()
        : ValidationResult.Invalid(string.Join("; ", errors));
})
```

## Tuple Requests

For operations with multiple parameters:

```csharp
// Define with tuple
spec.Operation<(string AccountId, decimal Amount), ApiResult<decimal>>("Withdraw", (request, state) =>
{
    var (accountId, amount) = request;  // Destructure
    // ... logic using accountId and amount
});

// Create inputs
var inputs = new InputSet
{
    spec.GetOperation<(string, decimal), ApiResult<decimal>>("Withdraw")
        .With(("alice", 50m), "Withdraw 50 from alice"),
};

// Bind execution
spec.ExecuteWith<BankApiClient>()
    .Bind<(string, decimal), ApiResult<decimal>>("Withdraw",
        (client, req) => client.Withdraw(req.Item1, req.Item2).Result);
```

## State Reset Patterns

### Delete Known Entities

```csharp
var knownIds = new[] { "alice", "bob", "9am", "10am" };

var results = await spec.RunTests(context, initialState, testCases, new TestExecutionOptions
{
    BeforeEachAsync = async _ =>
    {
        foreach (var id in knownIds)
        {
            try { await client.Delete(id); }
            catch { /* Ignore 404 */ }
        }
    }
});
```

### Recreate Test Container

```csharp
var results = await spec.RunTests(context, initialState, testCases, new TestExecutionOptions
{
    BeforeEachAsync = async _ =>
    {
        await _testContainer.ResetAsync();
    }
});
```

### Unique Names Per Test Run

```csharp
var testRunId = Guid.NewGuid().ToString("N")[..8];

var inputs = new InputSet
{
    createOp.With($"user-{testRunId}-1", "Create user 1"),
    createOp.With($"user-{testRunId}-2", "Create user 2"),
};
```

## Next Steps

- **Troubleshooting**: Common mistakes and debugging
- **Test Generation**: Configure exploration and algorithms
