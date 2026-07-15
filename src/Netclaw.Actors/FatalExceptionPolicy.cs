// -----------------------------------------------------------------------
// <copyright file="FatalExceptionPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors;

internal static class FatalExceptionPolicy
{
    public static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException or
        StackOverflowException or
        AccessViolationException;
}
