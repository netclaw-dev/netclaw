---
name: accordant-concurrency
description: How to test for race conditions using concurrent tests and linearizability - use this skill when testing concurrent operations or finding race condition bugs
---

# Concurrency Testing in Accordant

Accordant can test concurrent operations to find race conditions. It runs operations simultaneously and validates that results are **linearizable** — explainable by some sequential ordering.

## The Problem: Race Conditions

Consider a booking system where each slot can only be booked once:

```
Slot "9am" is available.

Concurrent requests:
  - Alice: BookSlot("9am")
  - Bob: BookSlot("9am")

Valid outcomes (one must fail):
  ✓ Alice succeeds, Bob gets Conflict
  ✓ Bob succeeds, Alice gets Conflict

Invalid outcome (BUG!):
  ✗ Both succeed — double booking!
```

## Linearizability

The correctness criterion for concurrent operations:

> Results must be explainable by **some** sequential ordering of the operations.

If Alice got 200 and Bob got 409 → Valid (as if Alice went first)
If both got 200 → Invalid (no sequential order explains this)

## Defining the Spec

```csharp
[State]
public partial class BookingState
{
    public Dictionary<string, string?> Slots { get; set; } = new();  // null = available
}

var spec = new Spec<BookingState>();

spec.Operation<string, ApiResult<Slot>>("CreateSlot", (slotId, state) =>
{
    if (state.Slots.ContainsKey(slotId))
        return Expect.That<ApiResult<Slot>>(r => r.IsConflict).SameState();

    return Expect.That<ApiResult<Slot>>(r => r.IsSuccess && r.Data.IsAvailable)
           .ThenState<BookingState>(s => s.Slots[slotId] = null);
});

spec.Operation<(string SlotId, string Customer), ApiResult<Slot>>("BookSlot", (request, state) =>
{
    var (slotId, customer) = request;

    if (!state.Slots.TryGetValue(slotId, out var bookedBy))
        return Expect.That<ApiResult<Slot>>(r => r.IsNotFound).SameState();

    if (bookedBy != null)
        return Expect.That<ApiResult<Slot>>(r => r.IsConflict, $"Already booked by {bookedBy}").SameState();

    return Expect.That<ApiResult<Slot>>(r => r.IsSuccess && r.Data.BookedBy == customer)
           .ThenState<BookingState>(s => s.Slots[slotId] = customer);
});
```

## Running Concurrent Tests

```csharp
[Test]
public async Task ConcurrentTests_FindsRaceConditions()
{
    var spec = CreateSpec();

    spec.ExecuteWith<BookingApiClient>()
        .Bind<string, ApiResult<Slot>>("CreateSlot", (c, id) => c.CreateSlot(id).Result)
        .Bind<(string, string), ApiResult<Slot>>("BookSlot", (c, r) => c.BookSlot(r.Item1, r.Item2).Result);

    var inputs = new InputSet
    {
        spec.GetOperation<string, ApiResult<Slot>>("CreateSlot").With("9am", "Create slot"),
        spec.GetOperation<(string, string), ApiResult<Slot>>("BookSlot").With(("9am", "Alice"), "Alice books"),
        spec.GetOperation<(string, string), ApiResult<Slot>>("BookSlot").With(("9am", "Bob"), "Bob books"),
    };

    var testCases = spec.GenerateConcurrentTests(
        new BookingState(),
        inputs,
        new TestGenerationOptions { MaxDepth = 4 });

    var context = spec.CreateTestingContext();
    context.Register(new BookingApiClient(CreateHttpClient()));

    var results = await spec.RunTests(
        context,
        new BookingState(),
        testCases,
        new TestExecutionOptions
        {
            BeforeEachAsync = async _ => await ResetDatabase()
        });

    var failures = results.Where(r => !r.Success).ToList();
    Assert.IsEmpty(failures, $"Failed: {failures.FirstOrDefault()?.LastFailureMessage}");
}
```

## How Concurrent Tests Work

1. **Sequential prefix** sets up a known state
2. **N operations** execute simultaneously
3. **All N! orderings** are checked for linearizability
4. If **any** ordering explains the results → Valid
5. If **no** ordering works → Race condition found!

### Example Test Case

```
Sequential: CreateSlot("9am")
Concurrent: [BookSlot(Alice)] || [BookSlot(Bob)]
```

### Validation

Accordant tries both orderings:

**Try Alice → Bob:**
- BookSlot(Alice) from available → expects Success
- BookSlot(Bob) from booked → expects Conflict

**Try Bob → Alice:**
- BookSlot(Bob) from available → expects Success
- BookSlot(Alice) from booked → expects Conflict

If actual results match either ordering → Valid

## MaxConcurrencyLevel

Control how many operations run concurrently:

```csharp
var options = new TestGenerationOptions
{
    MaxConcurrencyLevel = 3  // Up to 3 concurrent operations
};
```

Higher concurrency finds more bugs but increases test count (N! orderings to check).

## Finding the Bug

When a race condition exists, Accordant reports:

```
FAILURE in concurrent test case:
  Sequential prefix: CreateSlot("9am")
  Concurrent: BookSlot(Alice) || BookSlot(Bob)

  Observed results:
    BookSlot(Alice) → 200 OK, slot booked by Alice
    BookSlot(Bob) → 200 OK, slot booked by Bob

  ERROR: No linearization found!
    - If Alice first: Bob should get 409 Conflict
    - If Bob first: Alice should get 409 Conflict
    - Both succeeded → RACE CONDITION BUG
```

## Fixing Race Conditions

Common approaches:

### Database-Level Locking

```csharp
using var transaction = await _db.Database.BeginTransactionAsync(
    IsolationLevel.Serializable);
```

### Optimistic Concurrency (ETags)

```csharp
var slot = await _db.Slots.FindAsync(slotId);
if (slot.ConcurrencyToken != expectedToken)
    throw new ConcurrencyException();
```

### Application-Level Lock

```csharp
private static readonly SemaphoreSlim _lock = new(1, 1);

await _lock.WaitAsync();
try { /* booking logic */ }
finally { _lock.Release(); }
```

## Common Race Condition Patterns

### Double-Booking

Two users book the same resource simultaneously:

```csharp
// Both execute this check at the same time, both see "available"
if (slot.IsAvailable)
{
    slot.BookedBy = customer;  // Both write!
    await _db.SaveChangesAsync();
}
```

### Lost Updates

Two concurrent updates, one overwrites the other:

```csharp
// Thread A reads balance = 100
// Thread B reads balance = 100
// Thread A: balance = 100 + 50 = 150, saves
// Thread B: balance = 100 + 30 = 130, saves (overwrites A!)
```

### Counter Races

Increment operations that don't use atomic operations:

```csharp
// Wrong - not atomic
var count = await GetCount();
await SetCount(count + 1);

// Right - use atomic increment
await _db.ExecuteSqlRawAsync("UPDATE counters SET value = value + 1 WHERE id = @id");
```

## Test Case Structure

Concurrent test cases have:

1. **Sequential prefix**: Operations to reach a specific state
2. **Concurrent operations**: 2+ operations that fire simultaneously
3. **Optional sequential suffix**: Verify final state

```csharp
// Generated test case structure:
// Prefix: CreateSlot("9am") → Slot available
// Concurrent: [BookSlot(Alice), BookSlot(Bob)]
// Suffix: GetSlot("9am") → Verify winner
```

## Best Practices

1. **Test write-write conflicts**: Focus on operations that modify the same data
2. **Include read-after-write**: Add GET operations to verify final state
3. **Keep concurrency level reasonable**: 2-3 concurrent ops usually sufficient
4. **Reset state properly**: Concurrent tests are sensitive to leftover state
5. **Run multiple times**: Race conditions are timing-dependent

## Next Steps

- **Async Operations**: Model background work that completes over time
- **Troubleshooting**: Debug failing concurrent tests
