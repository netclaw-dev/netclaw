// -----------------------------------------------------------------------
// <copyright file="SecretOutputRedactorLinearTimeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// The redactor runs on every tool result, more than once, on inputs up to megabytes. It must
/// stay linear on repetitive and adversarial input, and the change that made it linear must not
/// have moved a single match: the output is compared byte for byte with the previous redactor.
/// </summary>
[Collection(RedactorTimingCollection.Name)]
public sealed class SecretOutputRedactorLinearTimeTests
{
    // The quadratic patterns needed seconds per megabyte (minutes for a private key).
    // RedactorTimingBudget sets the budget for linear work.
    private const int AdversarialChars = 4 * 1024 * 1024;

    public static TheoryData<string> ShapeNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in RedactorComparisonInputs.Shapes())
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void Redact_finishes_adversarial_input_within_budget(string shape)
    {
        var input = RedactorComparisonInputs.Shapes().Single(s => s.Name == shape).Build(AdversarialChars);

        RedactorTimingBudget.AssertFinishesWithinBudget(shape, input);
    }

    [Theory]
    [InlineData("token_")]
    [InlineData("api_key_")]
    [InlineData("secret_")]
    [InlineData("password_")]
    public void Redact_stays_linear_on_long_runs_of_a_secret_word(string unit)
    {
        var input = string.Concat(Enumerable.Repeat(unit, 4_000_000 / unit.Length));

        var redacted = RedactorTimingBudget.AssertFinishesWithinBudget(unit, input);

        Assert.Equal(input, redacted);
    }

    [Fact]
    public void Redact_masks_a_private_key_that_follows_a_run_of_unterminated_headers()
    {
        var input = string.Concat(Enumerable.Repeat("-----BEGIN PRIVATE KEY-----", 200))
            + "\n-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJBAK\n-----END RSA PRIVATE KEY-----\ntail";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.DoesNotContain("MIIBOgIBAAJBAK", redacted, StringComparison.Ordinal);
        Assert.EndsWith("***REDACTED***\ntail", redacted, StringComparison.Ordinal);
    }

    // The shapes at a size the old, quadratic patterns can still handle.
    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void Redact_masks_at_least_what_the_old_redactor_masked_on_repetitive_shapes(string shape)
    {
        var input = RedactorComparisonInputs.Shapes().Single(s => s.Name == shape).Build(24 * 1024);

        AssertMasksAtLeastWhatTheOldRedactorMasked(input);
    }

    // These comparisons first required byte-identical output. Issue #2595 adds shapes that the
    // old redactor did not mask (URL credentials, option values, headers, name and value pairs),
    // so the current output can mask more. The rule is now a superset rule: text that the
    // current redactor keeps, the old redactor also kept.
    //
    // The check splits the input into probes at whitespace and at the characters that a
    // replacement can write itself ('"', ':', '=', ';', '*'). A probe that is not part of the
    // mask text cannot overlap a mask or a written character. Thus each probe occurrence in the
    // current output is input text that the current redactor kept. When the old redactor kept
    // that text too, the occurrence is also in the old output. So the count of each probe in the
    // current output must not be more than its count in the old output.
    private static readonly char[] ProbeSeparators = [' ', '\t', '\r', '\n', '"', ':', '=', ';', '*'];

    private static void AssertMasksAtLeastWhatTheOldRedactorMasked(string input)
    {
        var old = PreLinearSecretOutputRedactor.Redact(input);
        var current = SecretOutputRedactor.Redact(input);
        if (string.Equals(old, current, StringComparison.Ordinal))
            return;

        foreach (var probe in input.Split(ProbeSeparators, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
        {
            if (probe.Length < 2 || Mask.Contains(probe, StringComparison.Ordinal))
                continue;

            var inCurrent = CountOverlapping(current, probe);
            var inOld = CountOverlapping(old, probe);
            Assert.True(inCurrent <= inOld,
                $"probe [{Shorten(probe)}] is in the current output {inCurrent} times but in the old output {inOld} times\n"
                + $"input [{Shorten(input)}]\nold     [{Shorten(old)}]\ncurrent [{Shorten(current)}]");
        }
    }

    private const string Mask = "***REDACTED***";

    private static int CountOverlapping(string text, string probe)
    {
        var count = 0;
        for (var at = text.IndexOf(probe, StringComparison.Ordinal); at >= 0; at = text.IndexOf(probe, at + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Shorten(string s) => (s.Length > 300 ? s[..300] + "..." : s).Replace("\n", "\\n");

    [Fact]
    public void Redact_masks_at_least_what_the_old_redactor_masked_on_generated_inputs()
    {
        var checkedInputs = 0;
        var masked = 0;
        foreach (var input in RedactorComparisonInputs.Random(seed: 2401, count: 120_000))
        {
            AssertMasksAtLeastWhatTheOldRedactorMasked(input);
            checkedInputs++;
            if (!string.Equals(SecretOutputRedactor.Redact(input), input, StringComparison.Ordinal))
                masked++;
        }

        // A comparison of inputs nothing happens to proves nothing.
        Assert.Equal(120_000, checkedInputs);
        Assert.True(masked > 20_000, $"only {masked} generated inputs contained anything to mask");
    }
}

/// <summary>Timing tests must not share the machine with other test classes running in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RedactorTimingCollection
{
    public const string Name = "RedactorTiming";
}

/// <summary>
/// Measures the CPU time of Redact on adversarial input against a budget that scales with the
/// speed of the machine. Wall-clock time is not a valid measure on a shared CI runner, because
/// other processes take the cores. On one Windows run, the benign baseline took 26 ms. On a
/// machine of that speed, "dense header lines" needs about 0.75 s of CPU time, but that run did
/// not finish in 5 s of wall-clock time.
/// </summary>
internal static class RedactorTimingBudget
{
    // The baseline is the CPU time of Redact on benign text of the same length, on the same
    // machine. The benign line has no key=value text, so it stays on the cheap path (about 30 ms
    // for 4 MB). Measured in baselines of CPU time on an idle machine, the slowest linear shapes
    // need about 35 ("repeat \"token\":\"", "dense option matches": 0.9 s to 1.0 s). The known
    // '+' regression of JsonSecretHeaderRegex needs about 1000 (30 s). The factor 100 leaves
    // about 3 times margin above the linear shapes and 10 times margin below the regression. The
    // 4 s floor is above every linear shape and below the regression.
    private const double BaselineFactor = 100;
    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(4);
    private const string BenignLine = "INFO ok\n";

    // Windows updates process CPU time once for each clock tick (about 16 ms). One benign run is
    // only a few ticks, so the baseline is the mean of several runs.
    private const int BaselineRuns = 5;

    // The wait checks the CPU time at this interval. It stops when the worker finishes or when
    // the worker uses all of its CPU budget.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(250);

    // A worker that cannot get a core for this long is a failure too. This limit is for a stalled
    // runner, not for the redactor.
    private static readonly TimeSpan WallLimit = TimeSpan.FromMinutes(2);

    public static string AssertFinishesWithinBudget(string shape, string input)
    {
        var baseline = MeasureBaseline(input.Length);
        var budget = TimeSpan.FromTicks(Math.Max(Floor.Ticks, (long)(baseline.Ticks * BaselineFactor)));

        // RedactorTimingCollection runs alone, so the CPU time of this process is the CPU time of
        // the worker, its garbage collections, and the JIT.
        var cpuStart = Environment.CpuUsage.TotalTime;
        var stopwatch = Stopwatch.StartNew();
        string? redacted = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                // Include warmup in the guard; a bad pattern can stall on the prefix too.
                SecretOutputRedactor.Redact(input[..Math.Min(input.Length, 64 * 1024)]);
                redacted = SecretOutputRedactor.Redact(input);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true };
        worker.Start();

        // A quadratic pattern cannot be interrupted, so stop the wait when it has used its budget.
        var finished = false;
        var cpu = TimeSpan.Zero;
        while (!finished && cpu <= budget && stopwatch.Elapsed < WallLimit)
        {
            finished = worker.Join(CheckInterval);
            cpu = Environment.CpuUsage.TotalTime - cpuStart;
        }

        var elapsed = stopwatch.Elapsed;

        // Limit the cleanup wait. A failed guard can leave its worker active until the test process ends.
        if (!finished)
            worker.Join(TimeSpan.FromSeconds(60));

        Assert.True(finished && cpu <= budget,
            $"{shape}: {input.Length / (1024.0 * 1024.0):F1} MB did not finish within the CPU budget. "
            + $"cpu {cpu.TotalSeconds:F2} s, budget {budget.TotalSeconds:F2} s, baseline {baseline.TotalSeconds:F3} s, "
            + $"elapsed {elapsed.TotalSeconds:F2} s, finished {finished}");
        Assert.Null(failure);
        return Assert.IsType<string>(redacted);
    }

    private static TimeSpan MeasureBaseline(int length)
    {
        var benign = string.Concat(Enumerable.Repeat(BenignLine, length / BenignLine.Length + 1))[..length];
        var cpuStart = Environment.CpuUsage.TotalTime;
        for (var run = 0; run < BaselineRuns; run++)
            SecretOutputRedactor.Redact(benign);

        return (Environment.CpuUsage.TotalTime - cpuStart) / BaselineRuns;
    }
}
