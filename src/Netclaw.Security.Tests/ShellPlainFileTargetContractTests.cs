// -----------------------------------------------------------------------
// <copyright file="ShellPlainFileTargetContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using Netclaw.Security.Authorization.Filesystem;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellPlainFileTargetContractTests
{
    private const string WorkingDirectory = "/work/project";

    private static readonly ShellExecutionEnvironment Bash52 =
        ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2));

    [Fact]
    public void Incomplete_exact_target_does_not_resolve()
        => AssertUnresolved(CreateRedirect(Exact("/work/project/result.txt"), isComplete: false), WorkingDirectory);

    [Fact]
    public void Unknown_target_does_not_resolve()
        => AssertUnresolved(CreateRedirect(CreateUnknown(), isComplete: true), WorkingDirectory);

    [Fact]
    public void Finite_target_does_not_resolve()
        => AssertUnresolved(
            CreateRedirect(CreateFiniteSet(["/work/project/first.txt", "/work/project/second.txt"]), isComplete: true),
            WorkingDirectory);

    [Fact]
    public void Empty_exact_target_does_not_resolve()
        => AssertUnresolved(CreateRedirect(Exact(string.Empty), isComplete: true), WorkingDirectory);

    [Fact]
    public void Invalid_exact_path_does_not_resolve()
        => AssertUnresolved(CreateRedirect(Exact("../../../escape.txt"), isComplete: true), WorkingDirectory);

    [Fact]
    public void Relative_exact_target_needs_a_base()
        => AssertUnresolved(CreateRedirect(Exact("result.txt"), isComplete: true), workingDirectory: null);

    [Fact]
    public void Absolute_exact_target_resolves()
    {
        var redirect = CreateRedirect(Exact("/work/project/result.txt"), isComplete: true);

        Assert.True(ShellCommandAnalysis.TryResolvePlainFileTarget(
            redirect, WorkingDirectory, ShellPathStyle.Posix, out var path));
        Assert.Equal("/work/project/result.txt", path.Value);
    }

    [Fact]
    public void Relative_exact_target_resolves_with_a_base()
    {
        var redirect = CreateRedirect(Exact("result.txt"), isComplete: true);

        Assert.True(ShellCommandAnalysis.TryResolvePlainFileTarget(
            redirect, WorkingDirectory, ShellPathStyle.Posix, out var path));
        Assert.Equal("/work/project/result.txt", path.Value);
    }

    [Fact]
    public void Null_device_is_a_valid_plain_file_target()
    {
        var redirect = CreateRedirect(Exact("/dev/null"), isComplete: true);

        Assert.True(ShellCommandAnalysis.TryResolvePlainFileTarget(
            redirect, WorkingDirectory, ShellPathStyle.Posix, out var path));
        Assert.Equal("/dev/null", path.Value);
    }

    [Fact]
    public void Bash_network_device_is_not_a_plain_file_target()
        => AssertUnresolved(CreateRedirect(Exact("/dev/tcp/host/80"), isComplete: true), WorkingDirectory);

    private static FileRedirectAnalysis CreateRedirect(ShellValueDomain target, bool isComplete)
    {
        var parsed = new ShellCommandAnalyzer(Bash52).Analyze(
            "printf x > /work/project/seed.txt",
            WorkingDirectory);
        var redirect = Assert.IsType<FileRedirectAnalysis>(
            Assert.Single(Assert.Single(parsed.Commands).Redirects));
        SetInternalProperty(redirect, nameof(FileRedirectAnalysis.Target), target);
        SetInternalProperty(redirect, nameof(RedirectAnalysis.IsComplete), isComplete);
        return redirect;
    }

    private static void AssertUnresolved(FileRedirectAnalysis redirect, string? workingDirectory)
    {
        Assert.False(ShellCommandAnalysis.TryResolvePlainFileTarget(
            redirect, workingDirectory, ShellPathStyle.Posix, out var path));
        Assert.Null(path.Value);
    }

    private static ShellValueDomain Exact(string value)
        => CreateDomain<ShellValueDomain.Exact>(value);

    private static ShellValueDomain CreateUnknown()
        => CreateDomain<ShellValueDomain.Unknown>();

    private static ShellValueDomain CreateFiniteSet(IReadOnlyList<string> values)
        => CreateDomain<ShellValueDomain.FiniteSet>(values);

    private static TDomain CreateDomain<TDomain>(params object?[] arguments)
        where TDomain : ShellValueDomain
    {
        var constructor = typeof(TDomain)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == arguments.Length
                    && parameters.Select((parameter, index) =>
                        arguments[index] is { } value
                            ? parameter.ParameterType.IsInstanceOfType(value)
                            : !parameter.ParameterType.IsValueType).All(static matches => matches);
            });
        return Assert.IsType<TDomain>(constructor.Invoke(arguments));
    }

    private static void SetInternalProperty(object target, string name, object? value)
    {
        var property = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var setter = property?.GetSetMethod(nonPublic: true);
        Assert.NotNull(setter);
        setter.Invoke(target, [value]);
    }
}
