// -----------------------------------------------------------------------
// <copyright file="SecretOutputRedactorOldBehaviourTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// The redactor may learn new formats and names, but it must never stop masking something the
/// released redactor masked. This runs both redactors over a generated grid of
/// (secret shape x text before x text after) and requires that every secret the old one masked
/// is masked by the new one.
/// </summary>
public sealed class SecretOutputRedactorOldBehaviourTests
{
    private static string Pad(string prefix, int length, char fill = 'a') => prefix + new string(fill, length);

    // Each unit is text that contains a secret the 0.27.1 redactor knew how to mask,
    // plus the substring that must not survive. Values are assembled at run time.
    private static IEnumerable<(string Name, string Unit, string Secret)> Units()
    {
        var tokens = new (string Name, string Token)[]
        {
            ("openai", Pad("sk-", 30, 'k')),
            ("openai-underscore-dash", "sk-proj-" + new string('k', 20) + "_" + new string('K', 10) + "-" + new string('q', 8)),
            ("anthropic", "sk-ant-api03-" + new string('Z', 40)),
            ("slack-bot", "xoxb-1234567890-1234567890123-" + new string('c', 24)),
            ("slack-user", "xoxp-1234567890-1234567890-1234567890-" + new string('d', 32)),
            ("slack-legacy", "xoxa-2-1234567890-" + new string('f', 24)),
            ("slack-refresh", "xoxr-1234567890-" + new string('r', 24)),
            ("github-pat", Pad("ghp_", 36)),
            ("github-pat-20", Pad("ghp_", 20, 'B')),
            ("aws", "AKIA" + new string('A', 16)),
            ("aws-digits", "AKIA" + "1234567890" + "ABCDEF"),
            ("jwt", "eyJ" + new string('a', 12) + ".eyJ" + new string('b', 12) + "." + new string('c', 12)),
        };

        foreach (var (name, token) in tokens)
            yield return (name, token, token);

        foreach (var (name, token) in tokens.Take(3))
            yield return ("bearer-" + name, "Authorization: Bearer " + token, token);

        yield return ("slack-webhook", "https://hooks.slack.com/services/" + "T0" + "ABCDEF/B0" + "ABCDEF/" + new string('x', 24), "T0" + "ABCDEF/B0" + "ABCDEF/" + new string('x', 24));
        yield return ("private-key", "-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJBAK\n-----END RSA PRIVATE KEY-----", "MIIBOgIBAAJBAK");

        var names = new[]
        {
            "token", "TOKEN", "api_key", "API-KEY", "apikey", "client_secret", "password", "PASSWORD",
            "refresh_token", "access-token", "private_key", "connection_string", "credential", "secret",
            "token_file", "authorization", "secret_santa"
        };
        var values = new[] { "plainvalue", "hunter2", "abc-def_123", "wJalrXUtnFEMI/K7MDENG" };
        foreach (var name in names)
        {
            foreach (var value in values)
            {
                yield return ($"env-{name}", $"{name}={value}", value);
                yield return ($"json-{name}", $"\"{name}\": \"{value}\"", value);
            }
        }

        foreach (var value in values)
            yield return ("connstring", $"Server=db;Password={value};Port=1", value);
    }

    private static readonly string[] Before =
    [
        "", " ", "key ", "token: ", "\"", "'", "(", "[", "/", "-", ".", "x_", "x", "\n", "--", "export ", "?", "&"
    ];

    private static readonly string[] After =
    [
        "", " rest", "\n", "-x", "_x", ".x", "/x", "-", "_", "--", ".", ",", ")", "]", "\"", ";", "&b=c",
        "-backup", "-old", "x", new string('Z', 40), new string('7', 40), "=", ":", "?x=1"
    ];

    [Fact]
    public void Everything_the_released_redactor_masked_is_still_masked()
    {
        var inputs = 0;
        var maskedByOld = 0;
        var regressions = new List<string>();

        foreach (var (name, unit, secret) in Units())
        {
            foreach (var before in Before)
            {
                foreach (var after in After)
                {
                    var input = before + unit + after;
                    inputs++;

                    var oldOutput = Pre2401SecretOutputRedactor.Redact(input);
                    if (oldOutput.Contains(secret, StringComparison.Ordinal))
                        continue; // the released redactor did not mask this one

                    maskedByOld++;
                    var newOutput = SecretOutputRedactor.Redact(input);
                    if (newOutput.Contains(secret, StringComparison.Ordinal))
                        regressions.Add($"{name} :: [{input.Replace("\n", "\\n")}] old=[{oldOutput.Replace("\n", "\\n")}] new=[{newOutput.Replace("\n", "\\n")}]");
                }
            }
        }

        // Guard the grid itself: a test that compares nothing proves nothing.
        Assert.True(inputs >= 500, $"only {inputs} inputs generated");
        Assert.True(maskedByOld >= 500, $"the released redactor masked only {maskedByOld} inputs");
        Assert.True(regressions.Count == 0,
            $"{regressions.Count} of {maskedByOld} inputs the released redactor masked now leak:\n" + string.Join("\n", regressions.GroupBy(r => r[..r.IndexOf(" :: ", StringComparison.Ordinal)]).SelectMany(g => g.Take(3))));
    }
}
