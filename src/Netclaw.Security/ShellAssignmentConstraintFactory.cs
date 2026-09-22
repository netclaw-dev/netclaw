// -----------------------------------------------------------------------
// <copyright file="ShellAssignmentConstraintFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Netclaw.Configuration;
using ShellSyntaxTree;

namespace Netclaw.Security;

internal static class ShellAssignmentConstraintFactory
{
    private const uint FormatVersion = 1;
    private static readonly byte[] Magic = "NCAS"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryCreate(
        ApprovalShell shell,
        IReadOnlyList<ShellVariableAssignment> assignments,
        out ApprovalAssignmentConstraint constraint)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        constraint = ApprovalAssignmentConstraint.None;
        if (assignments.Count == 0)
            return true;

        var shellByte = shell switch
        {
            ApprovalShell.Bash => (byte)1,
            ApprovalShell.PowerShell => (byte)2,
            _ => (byte)0,
        };
        if (shellByte == 0)
            return false;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Magic);
        AppendUInt32(hash, FormatVersion);
        hash.AppendData([shellByte]);
        AppendUInt32(hash, (uint)assignments.Count);

        try
        {
            foreach (var assignment in assignments)
            {
                if (assignment is null
                    || string.IsNullOrEmpty(assignment.Name)
                    || assignment.AuthoredValue is not ShellValueDomain.Exact
                    || assignment.EffectiveValue is not ShellValueDomain.Exact effective)
                {
                    return false;
                }

                var scopeByte = assignment.Scope switch
                {
                    ShellVariableAssignmentScope.ShellState => (byte)1,
                    ShellVariableAssignmentScope.CommandEnvironment => (byte)2,
                    _ => (byte)0,
                };
                if (scopeByte == 0)
                    return false;

                AppendString(hash, assignment.Name);
                hash.AppendData([scopeByte]);
                hash.AppendData([assignment.MayAffectProcessEnvironment ? (byte)1 : (byte)0]);
                AppendString(hash, effective.Value);
            }
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        var digest = new ApprovalAssignmentDigest(
            $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}");
        constraint = ApprovalAssignmentConstraint.ExactDigest(digest);
        return true;
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        AppendUInt32(hash, checked((uint)bytes.Length));
        hash.AppendData(bytes);
    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
