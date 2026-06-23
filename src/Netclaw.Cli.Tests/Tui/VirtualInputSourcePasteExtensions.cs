// -----------------------------------------------------------------------
// <copyright file="VirtualInputSourcePasteExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using System.Threading.Channels;
using Termina.Input;

namespace Netclaw.Cli.Tests.Tui;

internal static class VirtualInputSourcePasteExtensions
{
    private static readonly FieldInfo InputChannelField = typeof(VirtualInputSource)
        .GetField("_inputChannel", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Termina VirtualInputSource no longer exposes the expected input channel.");

    public static void EnqueuePaste(this VirtualInputSource input, string content)
    {
        ArgumentNullException.ThrowIfNull(input);

        var channel = (Channel<IInputEvent>)InputChannelField.GetValue(input)!;
        channel.Writer.TryWrite(new PasteEvent(content));
    }
}
