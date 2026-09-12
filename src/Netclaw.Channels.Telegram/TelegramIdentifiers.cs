// -----------------------------------------------------------------------
// <copyright file="TelegramIdentifiers.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Telegram;

public readonly record struct TelegramChatId(long Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct TelegramMessageId(int Value)
{
    public override string ToString() => Value.ToString();
}
