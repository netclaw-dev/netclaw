// -----------------------------------------------------------------------
// <copyright file="PairCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Daemon;

/// <summary>
/// Verifies recovery guidance and the credential persistence boundary for device pairing.
/// </summary>
public sealed class PairCommandTests : IDisposable
{
    private const string Endpoint = "https://daemon.example";
    private const int MaximumResponseBytes = 4 * 1024;
    private static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(15);
    private readonly DisposableTempDir _directory = new();
    private readonly NetclawPaths _paths;

    public PairCommandTests()
    {
        _paths = new NetclawPaths(_directory.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task DuplicateName_RecommendsSameCodeWithoutSavingClientState()
    {
        using var response = ErrorResponse(HttpStatusCode.Conflict, "Device name already exists.");

        var result = await RunAsync(response);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("different device name", result.Stderr);
        Assert.Contains("same unexpired pairing code", result.Stderr);
        Assert.DoesNotContain("netclaw daemon pair", result.Stderr);
        AssertNoClientState();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "invalid, expired, or already used")]
    [InlineData(HttpStatusCode.NotFound, "No active pairing code")]
    public async Task UnusableCode_RecommendsNewCodeWithoutSavingClientState(
        HttpStatusCode statusCode,
        string expectedReason)
    {
        using var response = ErrorResponse(statusCode, "Pairing code rejected.");

        var result = await RunAsync(response);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expectedReason, result.Stderr);
        Assert.Contains("netclaw daemon pair", result.Stderr);
        AssertNoClientState();
    }

    [Fact]
    public async Task RateLimit_HonorsRetryAfterWithoutSavingClientState()
    {
        using var response = ErrorResponse(HttpStatusCode.TooManyRequests, "Too many attempts.");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));

        var result = await RunAsync(response);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Wait at least 30 seconds", result.Stderr);
        Assert.DoesNotContain("netclaw daemon pair", result.Stderr);
        AssertNoClientState();
    }

    [Fact]
    public async Task SuccessfulExchange_SavesTokenAndEndpoint()
    {
        var body = new CountingReadStream(JsonSerializer.SerializeToUtf8Bytes(new { token = "device-token" }));
        using var response = StreamingJsonResponse(HttpStatusCode.OK, body);

        var result = await RunAsync(response);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(body.ContentLength, body.BytesRead);
        Assert.Equal(Endpoint, ClientConfigFile.ReadEndpoint(_paths));

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(secrets.TryGetValue("DeviceToken", out var storedToken));
        var protectedToken = storedToken is JsonElement element ? element.GetString() : storedToken?.ToString();
        Assert.Equal("device-token", ConfigFileHelper.DecryptIfEncrypted(_paths, protectedToken));
    }

    [Fact]
    public async Task SuccessBodyAtExactLimit_SavesTokenAndEndpoint()
    {
        var emptyResponseLength = JsonSerializer.SerializeToUtf8Bytes(new { token = string.Empty }).Length;
        var token = new string('X', MaximumResponseBytes - emptyResponseLength);
        var responseBytes = JsonSerializer.SerializeToUtf8Bytes(new { token });
        var body = new CountingReadStream(responseBytes);
        using var response = StreamingJsonResponse(HttpStatusCode.OK, body);

        var result = await RunAsync(response);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(MaximumResponseBytes, responseBytes.Length);
        Assert.Equal(MaximumResponseBytes, body.BytesRead);
        Assert.Equal(Endpoint, ClientConfigFile.ReadEndpoint(_paths));

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(secrets.TryGetValue("DeviceToken", out var storedToken));
        var protectedToken = storedToken is JsonElement element ? element.GetString() : storedToken?.ToString();
        Assert.Equal(token, ConfigFileHelper.DecryptIfEncrypted(_paths, protectedToken));
    }

    [Fact]
    public async Task NonLoopbackHttpEndpoint_FailsBeforeCodeInput()
    {
        var requestCount = 0;
        using var handler = new FakeHttpMessageHandler(_ =>
        {
            requestCount++;
            return FakeHttpMessageHandler.JsonResponse(new { token = "device-token" });
        });
        using var httpClient = new HttpClient(handler);

        var result = await RunAsync(httpClient, "http://daemon.example", "ABCD-EFGH\ntablet\n");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must use HTTPS", result.Stderr);
        Assert.DoesNotContain("Pairing code", result.Stdout);
        Assert.Equal(0, requestCount);
        AssertNoClientState();
    }

    [Fact]
    public async Task LoopbackHttpEndpoint_RemainsAvailable()
    {
        using var response = FakeHttpMessageHandler.JsonResponse(new { token = "device-token" });
        using var handler = new FakeHttpMessageHandler(_ => response);
        using var httpClient = new HttpClient(handler);

        var result = await RunAsync(httpClient, "http://127.0.0.1:5199", "ABCD-EFGH\ntablet\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("http://127.0.0.1:5199", ClientConfigFile.ReadEndpoint(_paths));
    }

    [Fact]
    public void PairingHttpHandler_DoesNotFollowRedirects()
    {
        using var handler = PairCommand.CreateHttpHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task RedirectResponse_FailsWithoutSavingClientState()
    {
        var requestCount = 0;
        using var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        {
            Headers = { Location = new Uri("https://redirect.example") },
        };
        using var handler = new FakeHttpMessageHandler(_ =>
        {
            requestCount++;
            return response;
        });
        using var httpClient = new HttpClient(handler);

        var result = await RunAsync(httpClient, Endpoint, "ABCD-EFGH\ntablet\n");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, requestCount);
        AssertNoClientState();
    }

    [Fact]
    public async Task InvalidSuccessJson_FailsWithoutSavingClientState()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{")
        };

        var result = await RunAsync(response);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("invalid JSON", result.Stderr);
        AssertNoClientState();
    }

    [Fact]
    public async Task UnsupportedSuccessEncoding_FailsWithoutSavingClientState()
    {
        using var response = FakeHttpMessageHandler.JsonResponse(new { token = "device-token" });
        response.Content.Headers.ContentType!.CharSet = "unsupported-encoding";

        var result = await RunAsync(response);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unsupported response encoding", result.Stderr);
        AssertNoClientState();
    }

    [Fact]
    public async Task Timeout_FailsWithoutSavingClientState()
    {
        using var handler = new TimeoutMessageHandler();
        using var httpClient = new HttpClient(handler);

        var result = await RunAsync(httpClient, Endpoint, "ABCD-EFGH\ntablet\n");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("did not respond before the timeout", result.Stderr);
        AssertNoClientState();
    }

    [Fact]
    public async Task ResponseBodyReadFailure_FailsWithoutSavingClientState()
    {
        var body = new FailingReadStream(
            new HttpIOException(HttpRequestError.ResponseEnded, "Controlled response body failure."));
        using var response = StreamingJsonResponse(HttpStatusCode.OK, body);

        var result = await RunAsync(response);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, body.ReadAttempts);
        Assert.Contains("response could not be read", result.Stderr);
        Assert.Contains("Controlled response body failure", result.Stderr);
        AssertNoClientState();
    }

    [Fact]
    public async Task OversizedErrorBody_IsNotCopiedToCliOutput()
    {
        var priorState = await SeedClientStateAsync();
        var marker = new string('X', 5_000);
        var body = new CountingReadStream(JsonSerializer.SerializeToUtf8Bytes(new { error = marker }));
        using var response = StreamingJsonResponse(HttpStatusCode.BadGateway, body);

        var result = await RunAsync(response);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(MaximumResponseBytes + 1, body.BytesRead);
        Assert.DoesNotContain(marker, result.Stderr);
        AssertClientStateUnchanged(priorState);
    }

    [Fact]
    public async Task OversizedSuccessBody_StopsAtLimitWithoutSavingClientState()
    {
        var priorState = await SeedClientStateAsync();
        var token = new string('X', 5_000);
        var body = new CountingReadStream(JsonSerializer.SerializeToUtf8Bytes(new { token }));
        using var response = StreamingJsonResponse(HttpStatusCode.OK, body);

        var result = await RunAsync(response);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(MaximumResponseBytes + 1, body.BytesRead);
        Assert.Contains($"exceeded {MaximumResponseBytes} bytes", result.Stderr);
        AssertClientStateUnchanged(priorState);
    }

    [Fact]
    public async Task ResponseDeadline_IncludesElapsedHeaderTimeWithoutSavingClientState()
    {
        var timeProvider = new FakeTimeProvider();
        var body = new CancellationOnlyStream();
        using var response = StreamingJsonResponse(HttpStatusCode.OK, body);
        using var handler = new DeferredHeadersMessageHandler(response);
        using var httpClient = new HttpClient(handler);

        var runTask = RunAsync(
            httpClient,
            Endpoint,
            "ABCD-EFGH\ntablet\n",
            timeProvider,
            TestContext.Current.CancellationToken);
        await handler.RequestStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        handler.ReleaseHeaders();
        await body.ReadStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        timeProvider.Advance(PairingTimeout - TimeSpan.FromSeconds(10));

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, body.ReadAttempts);
        Assert.Contains("did not respond before the timeout", result.Stderr);
        AssertNoClientState();
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(HttpResponseMessage response)
    {
        using var handler = new FakeHttpMessageHandler(_ => response);
        using var httpClient = new HttpClient(handler);
        return await RunAsync(httpClient, Endpoint, "ABCD-EFGH\ntablet\n");
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        HttpClient httpClient,
        string endpoint,
        string inputText)
        => await RunAsync(
            httpClient,
            endpoint,
            inputText,
            TimeProvider.System,
            CancellationToken.None);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        HttpClient httpClient,
        string endpoint,
        string inputText,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var input = new StringReader(inputText);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await PairCommand.RunAsync(
            ["pair", endpoint],
            _paths,
            httpClient,
            input,
            stdout,
            stderr,
            timeProvider,
            cancellationToken);

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode, string error)
        => FakeHttpMessageHandler.JsonResponse(new { error }, statusCode);

    private static HttpResponseMessage StreamingJsonResponse(HttpStatusCode statusCode, Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(statusCode) { Content = content };
    }

    private async Task<(byte[] Secrets, byte[] ClientConfig)> SeedClientStateAsync()
    {
        using var response = FakeHttpMessageHandler.JsonResponse(new { token = "prior-device-token" });
        var result = await RunAsync(response);

        Assert.Equal(0, result.ExitCode);
        return (File.ReadAllBytes(_paths.SecretsPath), File.ReadAllBytes(_paths.ClientConfigPath));
    }

    private void AssertClientStateUnchanged((byte[] Secrets, byte[] ClientConfig) priorState)
    {
        Assert.Equal(priorState.Secrets, File.ReadAllBytes(_paths.SecretsPath));
        Assert.Equal(priorState.ClientConfig, File.ReadAllBytes(_paths.ClientConfigPath));
    }

    private void AssertNoClientState()
    {
        Assert.False(File.Exists(_paths.SecretsPath));
        Assert.False(File.Exists(_paths.ClientConfigPath));
    }

    private sealed class TimeoutMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new TaskCanceledException("Test timeout."));
    }

    private sealed class DeferredHeadersMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _headers =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _requestStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RequestStarted => _requestStarted.Task;

        public void ReleaseHeaders() => _headers.SetResult(response);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestStarted.SetResult();
            return await _headers.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FailingReadStream(IOException exception) : MemoryStream
    {
        private int _readAttempts;

        public int ReadAttempts => Volatile.Read(ref _readAttempts);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readAttempts);
            return ValueTask.FromException<int>(exception);
        }
    }

    private sealed class CountingReadStream(byte[] content) : Stream
    {
        private int _offset;

        public int BytesRead { get; private set; }

        public int ContentLength => content.Length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadCore(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => ReadCore(buffer);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadCore(buffer.AsSpan(offset, count)));
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ReadCore(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, content.Length - _offset);
            content.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            BytesRead += count;
            return count;
        }
    }

    private sealed class CancellationOnlyStream : Stream
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readAttempts;

        public Task ReadStarted => _readStarted.Task;

        public int ReadAttempts => Volatile.Read(ref _readAttempts);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => WaitForCancellationAsync(cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => new(WaitForCancellationAsync(cancellationToken));

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readAttempts);
            _readStarted.TrySetResult();

            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            return await completion.Task;
        }
    }
}
