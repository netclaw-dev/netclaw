// -----------------------------------------------------------------------
// <copyright file="TelegramContractIds.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Tests.Channels.TestHelpers;

/// <summary>
/// Maps the contract base classes' string channel and user ids onto Telegram's
/// numeric chat, user, and message ids. Every fixture applies the same map to
/// the inbound message and to the options allowlists, so the string-form
/// comparisons inside <c>TelegramAclPolicy</c> and the gateway dedup key stay
/// consistent with the non-numeric literals the contract tests pass in.
/// </summary>
public static class TelegramContractIds
{
    /// <summary>
    /// Deterministic positive non-zero long for a contract id string. FNV-1a 64
    /// with the sign bit cleared; zero maps to one because chat id zero is not
    /// a real Telegram chat.
    /// </summary>
    public static long LongId(string value)
    {
        unchecked
        {
            var hash = 14695981039346656037UL;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 1099511628211UL;
            }

            var positive = (long)(hash & 0x7FFFFFFFFFFFFFFF);
            return positive == 0 ? 1 : positive;
        }
    }

    /// <summary>Deterministic positive non-zero int for a contract id string.</summary>
    public static int IntId(string value) => (int)(LongId(value) % int.MaxValue);
}
