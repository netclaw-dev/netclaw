// -----------------------------------------------------------------------
// <copyright file="SessionMediaStoreImageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Media;
using SkiaSharp;
using Xunit;

namespace Netclaw.Actors.Tests.Protocol;

/// <summary>
/// The session media store is the single normalization seam (#1296): both writers
/// bound images on write, so the egress read path never materializes an unbounded
/// payload. Fixtures are generated in-test with SkiaSharp.
/// </summary>
public sealed class SessionMediaStoreImageTests
{
    [Fact]
    public void WriteDataContent_bounds_oversized_chat_attachment_image()
    {
        using var dir = new TempDir();
        var src = GenerateImage(4000, 3000, SKEncodedImageFormat.Png);

        var reference = SessionMediaStore.WriteDataContent(new DataContent(src, "image/png"), dir.Path).Reference;

        Assert.NotNull(reference);
        var (w, h) = StoredDims(dir.Path, reference!);
        Assert.True(Math.Max(w, h) <= 1568, $"long edge {Math.Max(w, h)} exceeds cap");
        Assert.Equal("image/png", reference.MimeType.Value); // source format preserved (no transcode)
        Assert.Equal(StoredBytes(dir.Path, reference).Length, reference.FileSizeBytes);
    }

    [Fact]
    public void WriteDataContent_does_not_normalize_non_model_input_image()
    {
        using var dir = new TempDir();
        // BMP/TIFF are MediaKind.Image but NOT model-input-eligible, so they're never
        // inlined to a model — and must be stored byte-for-byte, not transcoded. (Real
        // BMP bytes aren't needed: the gate keys on MIME, so PNG bytes labeled image/bmp
        // prove the gate skips normalization.)
        var src = GenerateImage(4000, 3000, SKEncodedImageFormat.Png);

        var reference = SessionMediaStore.WriteDataContent(new DataContent(src, "image/bmp"), dir.Path).Reference;

        Assert.NotNull(reference);
        Assert.Equal("image/bmp", reference!.MimeType.Value); // unchanged
        Assert.Equal(src, StoredBytes(dir.Path, reference)); // byte-for-byte, not resized
    }

    [Fact]
    public void CopyFile_bounds_oversized_model_input_image()
    {
        using var dir = new TempDir();
        var srcPath = Path.Combine(dir.Path, "source.png");
        var src = GenerateImage(5000, 2000, SKEncodedImageFormat.Png);
        File.WriteAllBytes(srcPath, src);

        var reference = SessionMediaStore.CopyFile(
            srcPath, dir.Path, new MimeType("image/png"), MediaModality.Image, src.Length);

        Assert.NotNull(reference);
        var (w, h) = StoredDims(dir.Path, reference!);
        Assert.True(Math.Max(w, h) <= 1568, $"long edge {Math.Max(w, h)} exceeds cap");
    }

    [Fact]
    public void CopyFile_passes_non_image_media_through_unchanged()
    {
        using var dir = new TempDir();
        var srcPath = Path.Combine(dir.Path, "clip.mp3");
        var src = new byte[4096];
        new Random(3).NextBytes(src);
        File.WriteAllBytes(srcPath, src);

        var reference = SessionMediaStore.CopyFile(
            srcPath, dir.Path, new MimeType("audio/mpeg"), MediaModality.Audio, src.Length);

        Assert.NotNull(reference);
        Assert.Equal(src, StoredBytes(dir.Path, reference!)); // byte-for-byte, no decode
        Assert.Equal("audio/mpeg", reference.MimeType.Value);
    }

    [Fact]
    public void WriteDataContent_passes_small_image_through_unchanged()
    {
        using var dir = new TempDir();
        var src = GenerateImage(500, 400, SKEncodedImageFormat.Png);

        var reference = SessionMediaStore.WriteDataContent(new DataContent(src, "image/png"), dir.Path).Reference;

        Assert.NotNull(reference);
        Assert.Equal(src, StoredBytes(dir.Path, reference!)); // within caps → not re-encoded
        Assert.Equal("image/png", reference.MimeType.Value);
    }

    [Fact]
    public void WriteDataContent_drops_undecodable_image()
    {
        using var dir = new TempDir();
        var garbage = new byte[2048];
        new Random(9).NextBytes(garbage);

        var write = SessionMediaStore.WriteDataContent(new DataContent(garbage, "image/png"), dir.Path);

        Assert.Null(write.Reference); // refused, not persisted, never written raw
        Assert.NotNull(write.DroppedReason); // reason carried so the caller can surface a note
        Assert.Empty(MediaFiles(dir.Path)); // nothing written (media dir not even created)
    }

    [Fact]
    public void CopyFile_drops_undecodable_image()
    {
        using var dir = new TempDir();
        var srcPath = Path.Combine(dir.Path, "broken.png");
        var garbage = new byte[2048];
        new Random(11).NextBytes(garbage);
        File.WriteAllBytes(srcPath, garbage);

        var reference = SessionMediaStore.CopyFile(
            srcPath, dir.Path, new MimeType("image/png"), MediaModality.Image, garbage.Length);

        Assert.Null(reference);
        Assert.Empty(MediaFiles(dir.Path));
    }

    private static string[] MediaFiles(string sessionDir)
    {
        var mediaDir = Path.Combine(sessionDir, "media");
        return Directory.Exists(mediaDir) ? Directory.GetFiles(mediaDir) : [];
    }

    private static byte[] StoredBytes(string sessionDir, SerializableMediaReference reference)
        => File.ReadAllBytes(SessionMediaStore.GetMediaPath(sessionDir, reference.RelativePath));

    private static (int W, int H) StoredDims(string sessionDir, SerializableMediaReference reference)
    {
        using var data = SKData.CreateCopy(StoredBytes(sessionDir, reference));
        using var codec = SKCodec.Create(data);
        return (codec.Info.Width, codec.Info.Height);
    }

    private static byte[] GenerateImage(int width, int height, SKEncodedImageFormat format)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { IsAntialias = true };
            var rand = new Random(1234);
            var maxRadius = Math.Max(6, width / 10);
            for (var i = 0; i < 400; i++)
            {
                paint.Color = new SKColor(
                    (byte)rand.Next(256), (byte)rand.Next(256), (byte)rand.Next(256));
                canvas.DrawCircle(rand.Next(width), rand.Next(height), rand.Next(5, maxRadius), paint);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"netclaw-media-test-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
