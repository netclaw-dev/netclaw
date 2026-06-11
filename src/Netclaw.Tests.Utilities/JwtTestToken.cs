// -----------------------------------------------------------------------
// <copyright file="JwtTestToken.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.Json;

namespace Netclaw.Tests.Utilities;

internal static class JwtTestToken
{
    public static string Make(object payload)
        => MakeFromPayloadJson(JsonSerializer.Serialize(payload));

    public static string MakeFromPayloadJson(string payloadJson)
    {
        var header = Base64UrlEncode("{}");
        var body = Base64UrlEncode(payloadJson);
        return $"{header}.{body}.fakesig";
    }

    private static string Base64UrlEncode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
