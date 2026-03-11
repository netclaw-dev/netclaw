using Netclaw.MemoryRetrievalPoC.Tests.Prototype;
using Xunit;

namespace Netclaw.MemoryRetrievalPoC.Tests;

public sealed class RetrievalPrototypeTests : IDisposable
{
    private readonly RetrievalFixture _fixture = RetrievalFixture.Load();
    private readonly PrototypeSqliteStore _store = new();

    [Fact]
    public async Task Deterministic_retrieval_matches_expected_hits_and_no_hits()
    {
        await _store.InitializeAndSeedAsync(_fixture);

        var documents = await _store.LoadDocumentsAsync("project:signalr");
        var edges = await _store.LoadEdgesAsync("project:signalr");
        var engine = new DeterministicRecallEngine(documents, edges);

        var failures = new List<string>();
        foreach (var testCase in _fixture.Cases)
        {
            var hits = engine.Search(testCase.Prompt, 3);
            var bundle = engine.SearchBundle(testCase.Prompt);

            if (testCase.ExpectEmpty && hits.Count != 0)
            {
                failures.Add($"{testCase.Id}: expected empty but got [{string.Join(", ", hits.Select(x => x.DocumentId + "=" + x.Score.ToString("F1")))}]");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(testCase.ExpectedTopDocumentId))
            {
                var top = hits.FirstOrDefault()?.DocumentId;
                if (!string.Equals(top, testCase.ExpectedTopDocumentId, StringComparison.Ordinal))
                {
                    failures.Add($"{testCase.Id}: expected top {testCase.ExpectedTopDocumentId} but got {top ?? "<none>"}; hits=[{string.Join(", ", hits.Select(x => x.DocumentId + "=" + x.Score.ToString("F1") + "{" + string.Join("|", x.Reasons) + "}"))}]");
                }
            }

            if (testCase.ExpectedContainsDocumentIds is { Count: > 0 })
            {
                foreach (var expected in testCase.ExpectedContainsDocumentIds)
                {
                    if (!hits.Any(x => x.DocumentId == expected))
                        failures.Add($"{testCase.Id}: expected result set to include {expected}; hits=[{string.Join(", ", hits.Select(x => x.DocumentId))}]");
                }
            }

            if (testCase.ForbiddenDocumentIds is { Count: > 0 })
            {
                foreach (var forbidden in testCase.ForbiddenDocumentIds)
                {
                    if (hits.Any(x => x.DocumentId == forbidden))
                        failures.Add($"{testCase.Id}: forbidden hit {forbidden} surfaced");
                }
            }

            if (testCase.ExpectedBundle is { Count: > 0 })
            {
                foreach (var pair in testCase.ExpectedBundle)
                {
                    if (!bundle.Slots.TryGetValue(pair.Key, out var hit))
                    {
                        failures.Add($"{testCase.Id}: expected bundle slot {pair.Key} but it was missing; bundle=[{string.Join(", ", bundle.Slots.Select(x => x.Key + "=" + x.Value.DocumentId))}]");
                        continue;
                    }

                    if (!string.Equals(hit.DocumentId, pair.Value, StringComparison.Ordinal))
                        failures.Add($"{testCase.Id}: expected bundle slot {pair.Key} -> {pair.Value} but got {hit.DocumentId}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    public void Dispose() => _store.Dispose();
}
