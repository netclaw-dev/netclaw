// -----------------------------------------------------------------------
// <copyright file="TeamsAttachmentTransfer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Observes one HTTP attempt. The shared downloader still owns the byte limit and temporary file.
/// </summary>
internal sealed class TeamsAttachmentTransfer(TimeProvider timeProvider)
{
    internal static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(30);
    private readonly long _startedAt = timeProvider.GetTimestamp();
    private long? _headersAt;
    private long? _lastReadAt;

    internal string Stage { get; set; } = "request";
    internal long BytesRead { get; private set; }
    internal long? ContentLength { get; private set; }
    internal double ElapsedMilliseconds => timeProvider.GetElapsedTime(_startedAt).TotalMilliseconds;
    internal double? HeadersMilliseconds => _headersAt is { } at ? timeProvider.GetElapsedTime(_startedAt, at).TotalMilliseconds : null;
    internal double? LastReadAgeMilliseconds => _lastReadAt is { } at ? timeProvider.GetElapsedTime(at).TotalMilliseconds : null;

    internal void Observe(HttpResponseMessage response)
    {
        _headersAt = timeProvider.GetTimestamp();
        Stage = response.IsSuccessStatusCode ? "body" : "response_headers";
        ContentLength = response.Content.Headers.ContentLength;
        if (response.IsSuccessStatusCode)
            response.Content = new ObservedContent(response.Content, this);
    }

    private async ValueTask<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var idle = new CancellationTokenSource(ReadIdleTimeout, timeProvider);
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);
        int count;
        try
        {
            count = await stream.ReadAsync(buffer, read.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && idle.IsCancellationRequested)
        {
            // Await the cancelled read before disposal or retry. Never leave a detached read on the old response.
            throw new TeamsAttachmentReadTimeoutException();
        }

        if (count > 0)
        {
            BytesRead += count;
            _lastReadAt = timeProvider.GetTimestamp();
        }
        return count;
    }

    private sealed class ObservedContent : HttpContent
    {
        private readonly HttpContent _source;
        private readonly TeamsAttachmentTransfer _transfer;

        internal ObservedContent(HttpContent source, TeamsAttachmentTransfer transfer)
        {
            _source = source;
            _transfer = transfer;
            foreach (var header in source.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new ObservedStream(await _source.ReadAsStreamAsync().ConfigureAwait(false), _transfer);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new ObservedStream(await _source.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _transfer);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException("The Teams attachment body requires a bounded stream reader.");

        protected override bool TryComputeLength(out long length)
        {
            length = _source.Headers.ContentLength.GetValueOrDefault();
            return _source.Headers.ContentLength.HasValue;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _source.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ObservedStream(Stream source, TeamsAttachmentTransfer transfer) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            transfer.ReadAsync(source, buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // HttpContent owns the source stream. The downloader also disposes this view.
        // Keep ownership with the content so a retry closes the source exactly once.
    }
}

internal sealed class TeamsAttachmentReadTimeoutException() : TimeoutException("The Teams attachment body stopped responding.");
