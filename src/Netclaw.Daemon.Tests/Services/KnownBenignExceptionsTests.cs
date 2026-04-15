using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class KnownBenignExceptionsTests
{
    private const string RealStackMarker =
        "at SlackNet.ReconnectingWebSocket.Connect(Func`1 getWebSocketUrl, CancellationToken cancellationToken)";

    [Fact]
    public void IsSlackNetReconnectingWebSocketDisposeRace_Null_ReturnsFalse()
    {
        Assert.False(KnownBenignExceptions.IsSlackNetReconnectingWebSocketDisposeRace(null));
    }

    [Theory]
    [MemberData(nameof(PredicateCases))]
    public void IsSlackNetReconnectingWebSocketDisposeRace_MatchesExpected(
        Exception exception,
        bool expected)
    {
        Assert.Equal(expected, KnownBenignExceptions.IsSlackNetReconnectingWebSocketDisposeRace(exception));
    }

    public static IEnumerable<object[]> PredicateCases()
    {
        yield return [new InvalidOperationException("unrelated"), false];

        yield return [
            new FakeObjectDisposedException("cts", "at SomeOther.Library.Foo()"),
            false];

        yield return [
            new FakeObjectDisposedException("cts", stackTrace: null),
            false];

        yield return [
            new FakeObjectDisposedException("cts", RealStackMarker),
            true];

        yield return [
            new AggregateException(new FakeObjectDisposedException("cts", RealStackMarker)),
            true];

        yield return [
            new AggregateException(new InvalidOperationException("not slacknet")),
            false];

        // Multiple inners, one matches — Flatten().InnerExceptions must find it.
        yield return [
            new AggregateException(
                new InvalidOperationException("unrelated"),
                new FakeObjectDisposedException("cts", RealStackMarker)),
            true];

        // Nested aggregate with the match buried two levels deep.
        yield return [
            new AggregateException(
                new AggregateException(
                    new FakeObjectDisposedException("cts", RealStackMarker))),
            true];
    }

    private sealed class FakeObjectDisposedException : ObjectDisposedException
    {
        private readonly string? _stackTrace;

        public FakeObjectDisposedException(string objectName, string? stackTrace)
            : base(objectName)
        {
            _stackTrace = stackTrace;
        }

        public override string? StackTrace => _stackTrace;
    }
}
