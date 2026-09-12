// -----------------------------------------------------------------------
// <copyright file="TelegramChannelOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Channels;

namespace Netclaw.Channels.Telegram;

public sealed class TelegramChannelOptions : IRemoteChatChannelOptions
{
    public bool Enabled { get; init; }

    public SensitiveString? BotToken { get; init; }

    public bool AllowDirectMessages { get; init; }

    public bool MentionOnly { get; init; } = true;

    public string[] AllowedChatIds { get; init; } = [];

    public string[] AllowedUserIds { get; init; } = [];

    public Dictionary<string, string> ChatAudiences { get; init; } = new(StringComparer.Ordinal);
}
