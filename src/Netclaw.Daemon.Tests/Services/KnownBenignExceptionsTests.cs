using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class KnownBenignExceptionsTests
{
    [Fact]
    public void IsSlackNetReconnectingWebSocketDisposeRace_Null_ReturnsFalse()
    {
        Assert.False(KnownBenignExceptions.IsSlackNetReconnectingWebSocketDisposeRace(null));
    }

    [Fact]
    public void IsSlackNetReconnectingWebSocketDisposeRace_UnrelatedExceptionType_ReturnsFalse()
    {
        var exception = new InvalidOperationException("boom");
        Assert.False(KnownBenignExceptions.IsSlackNetReconnectingWebSocketDisposeRace(exception));
    }

    [Fact]
    public void IsSlackNetReconnectingWebSocketDisposeRace_OdeWithoutMarker_ReturnsFalse()
    {
        var exception = new FakeObjectDisposedException(
            objectName: "CancellationTokenSource",
            stackTrace: "at SomeOther.Library.Foo()\nat SomeOther.Library.Bar()");

        Assert.False(KnownBenignExceptions.IsSlackNetReconnectingWebSocketDisposeRace(exception));
    }

    [Fact]
    public void IsSlackNetReconnectingWebSocketDisposeRace_OdeWithMarker_ReturnsTrue()
    {
        var exception = new FakeObjectDisposedException(
            objectName: "CancellationTokenSource",
            stackTrace:
                "at System.Threading.CancellationTokenSource.get_Token()\n" +
                "at SlackNet.ReconnectingWebSocket.Connect(Func`1 getWebSocketUrl, CancellationToken cancellationToken)\n" +
                "at SlackNet.ReconnectingWebSocket.ReconnectOnClose(Func`1 getWebSocketUrl, CancellationToken cancellationToken)");

        Assert.True(KnownBenignExceptions.IsSlackNetReconnectingWebSocketDisposeRace(exception));
    }

    [Fact]
    public void IsSlackNetReconnectingWebSocketDisposeRace_AggregateWrappingMatchingOde_ReturnsTrue()
    {
        var inner = new FakeObjectDisposedException(
            objectName: "CancellationTokenSource",
            stackTrace: "at SlackNet.ReconnectingWebSocket.Connect(...)");

        var aggregate = new AggregateException(inner);

        Assert.True(KnownBenignExceptions.IsSlackNetReconnectingWebSocketDisposeRace(aggregate));
    }

    [Fact]
    public void IsSlackNetReconnectingWebSocketDisposeRace_AggregateWrappingUnrelatedException_ReturnsFalse()
    {
        var inner = new InvalidOperationException("not slacknet");
        var aggregate = new AggregateException(inner);

        Assert.False(KnownBenignExceptions.IsSlackNetReconnectingWebSocketDisposeRace(aggregate));
    }

    [Fact]
    public void IsSlackNetReconnectingWebSocketDisposeRace_OdeWithNullStackTrace_ReturnsFalse()
    {
        var exception = new FakeObjectDisposedException(
            objectName: "CancellationTokenSource",
            stackTrace: null);

        Assert.False(KnownBenignExceptions.IsSlackNetReconnectingWebSocketDisposeRace(exception));
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
