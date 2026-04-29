// -----------------------------------------------------------------------
// <copyright file="UpdateCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Netclaw.Cli.Update;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Configuration.Security;
using Netclaw.Tests.Utilities;
using NSec.Cryptography;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

[Collection("Update verification")]
public sealed class UpdateCommandTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly Key _testSigningKey;
    private readonly byte[] _testPublicKeyBlob;

    public UpdateCommandTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        _testSigningKey = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        var pubKeyRaw = _testSigningKey.Export(KeyBlobFormat.RawPublicKey);
        _testPublicKeyBlob = new byte[42];
        _testPublicKeyBlob[0] = 0x45;
        _testPublicKeyBlob[1] = 0x64;
        byte[] testKeyId = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        Array.Copy(testKeyId, 0, _testPublicKeyBlob, 2, 8);
        Array.Copy(pubKeyRaw, 0, _testPublicKeyBlob, 10, 32);

        MinisignVerifier.TestPublicKeyOverride = _testPublicKeyBlob;
        UpdateCheckService.ResetCache();
    }

    public void Dispose()
    {
        MinisignVerifier.TestPublicKeyOverride = null;
        UpdateCommand.TestHttpMessageHandlerFactory = null;
        UpdateCheckService.ResetCache();
        _testSigningKey.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task RunAsync_BlocksInstall_WhenDisableSelfUpdateIsConfigured()
    {
        var manifest = CreateManifest("99.0.0", UpdateCheckService.GetCurrentRid());
        UpdateCommand.TestHttpMessageHandlerFactory = () => CreateSignedHandler(manifest);

        using var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);

        try
        {
            var exitCode = await UpdateCommand.RunAsync(["update"], _paths, selfUpdateDisabled: true);

            Assert.Equal(1, exitCode);
            Assert.Contains("Self-update is disabled", stdout.ToString());
            Assert.Contains("Pull a newer container image to upgrade.", stdout.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private FakeHttpMessageHandler CreateSignedHandler(BinaryFeedManifest manifest)
    {
        var handler = new FakeHttpMessageHandler();
        var json = JsonSerializer.Serialize(manifest);
        handler.AddResponse(FeedConstants.BinaryManifestUrl, HttpStatusCode.OK, json, "application/json");

        var sigContent = SignContent(json);
        handler.AddResponse(FeedConstants.BinaryManifestSignatureUrl, HttpStatusCode.OK, sigContent, "text/plain");

        return handler;
    }

    private string SignContent(string content)
    {
        var data = Encoding.UTF8.GetBytes(content);
        var signature = SignatureAlgorithm.Ed25519.Sign(_testSigningKey, data);

        var sigBlob = new byte[74];
        sigBlob[0] = 0x45;
        sigBlob[1] = 0x44;
        Array.Copy(_testPublicKeyBlob, 2, sigBlob, 2, 8);
        Array.Copy(signature, 0, sigBlob, 10, 64);

        return $"untrusted comment: test signature\n{Convert.ToBase64String(sigBlob)}\ntrusted comment: test\ndGVzdA==\n";
    }

    private static BinaryFeedManifest CreateManifest(string version, string rid)
    {
        return new BinaryFeedManifest
        {
            Latest = version,
            UpdatedAt = DateTimeOffset.UtcNow,
            Releases =
            [
                new BinaryRelease
                {
                    Version = version,
                    ReleasedAt = DateTimeOffset.UtcNow,
                    Assets =
                    [
                        new BinaryAsset
                        {
                            Component = "netclaw",
                            Rid = rid,
                            Url = $"https://releases.netclaw.dev/{version}/netclaw-{version}-{rid}.tar.gz",
                            Sha256 = "abc123",
                            SizeBytes = 50_000_000
                        }
                    ]
                }
            ]
        };
    }

}
