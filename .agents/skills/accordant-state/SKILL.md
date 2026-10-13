---
name: accordant-state
description: How to design state models in Accordant - use this skill when defining what your system tracks between operations
---

# Designing State in Accordant

State is what an external observer needs to know to predict what an operation should return. Keep it minimal — only track what's necessary to define correct behavior.

## The [State] Attribute

Use `[State]` on a partial class to get automatic cloning, equality, and hashing:

```csharp
[State]
public partial class BankState
{
    public Dictionary<string, decimal> Accounts { get; set; } = new();
}
```

The source generator handles:
- **Deep cloning** for immutable state transitions
- **Equality comparison** for state deduplication
- **Hashing** for state graph exploration

## State Design Principles

### Keep It Minimal

Ask: "Does any operation need this to determine its response?" If no, leave it out.

```csharp
// ❌ Too detailed - includes implementation concerns
[State]
public partial class BadState
{
    public Dictionary<string, AccountEntity> Accounts { get; set; }  // EF entities
    public DateTime LastModified { get; set; }  // Not needed for behavior
    public string ConnectionString { get; set; }  // Implementation detail
}

// ✅ Just what operations need
[State]
public partial class GoodState
{
    public Dictionary<string, decimal> Accounts { get; set; } = new();  // Just balances
}
```

### Nested State Classes

For complex domains, nest state classes:

```csharp
[State]
public partial class AppState
{
    public Dictionary<string, UserState> Users { get; set; } = new();
}

[State]
public partial class UserState
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, TodoState> Todos { get; set; } = new();
}

[State]
public partial class TodoState
{
    public string Title { get; set; } = string.Empty;
    public bool Completed { get; set; } = false;
}
```

## State Transitions

Operations return expected outcomes that describe both the response and the next state.

### No State Change: `.SameState()`

For errors or read-only operations:

```csharp
// Error - doesn't modify state
if (!state.Accounts.ContainsKey(accountId))
    return Expect.That<ApiResult<decimal>>(r => r.IsNotFound).SameState();

// Read-only operation
return Expect.That<ApiResult<decimal>>(r => r.IsSuccess && r.Data == balance).SameState();
```

### State Changes: `.ThenState()`

The lambda receives a **clone** of the current state. Modify it directly:

```csharp
return Expect.That<ApiResult<decimal>>(r => r.IsSuccess && r.Data == 0)
       .ThenState<BankState>(nextState => nextState.Accounts[accountId] = 0);
```

**Convention**: Always name the parameter `nextState` for clarity.

### Response-Dependent State

When state depends on server-generated values (IDs, timestamps):

```csharp
return Expect.That<ApiResult<Order>>(r => r.IsSuccess && r.Data.OrderId != null)
       .ThenState<AppState>(
           (ApiResult<Order> response, AppState nextState) =>
               nextState.Orders[response.Data.OrderId] = new OrderState { /* ... */ },
           mock: () => new ApiResult<Order> { Data = new Order { OrderId = Guid.NewGuid().ToString() } }
       );
```

The `mock:` provides a plausible response for test generation (when no real system is running).

## Supported Property Types

| Type | Notes |
|------|-------|
| Primitives | `int`, `string`, `decimal`, `bool`, `DateTime`, `Guid`, etc. |
| Collections | `Dictionary<K,V>`, `List<T>`, `HashSet<T>` |
| Nested [State] | Other classes marked with `[State]` |
| Enums | Any enum type |

**Dictionary keys**: Supported types are `string`, `int`, `long`, and `Guid`. Keys are sorted during serialization to ensure consistent hashing.

## [SharedState] for Large Values

For large values where cloning would be expensive:

```csharp
[State]
public partial class ImageState
{
    public string Name { get; set; }

    [SharedState(nameof(ContentFingerprint))]
    public List<byte> Content { get; set; }

    // Fingerprint used for equality instead of serializing all bytes
    public string ContentFingerprint => Content == null
        ? null
        : Convert.ToHexString(SHA256.HashData(Content.ToArray()));
}
```

**Important**: Shared values are shallow-copied, so treat them as immutable. Never modify after setting.

## Common Mistakes

### Mutable State Access

```csharp
// ❌ Wrong - modifying original state
return Expect.That(r => r.IsSuccess)
       .ThenState<AppState>(nextState => {
           state.Users[id] = newUser;  // BAD: modifying 'state', not 'nextState'
           return nextState;
       });

// ✅ Correct - modify the clone
return Expect.That(r => r.IsSuccess)
       .ThenState<AppState>(nextState => nextState.Users[id] = newUser);
```

### Overly Complex State

```csharp
// ❌ Too complex - slows down state exploration
[State]
public partial class TooMuchState
{
    public List<AuditEntry> AuditLog { get; set; }  // Unbounded growth
    public Dictionary<string, List<HistoricalBalance>> BalanceHistory { get; set; }
}

// ✅ Focus on what determines behavior
[State]
public partial class JustRightState
{
    public Dictionary<string, decimal> Balances { get; set; }
}
```

## Next Steps

- **Operations**: Learn to write Apply methods that use state
- **Test Generation**: See how state complexity affects test count
