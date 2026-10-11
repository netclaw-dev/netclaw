// -----------------------------------------------------------------------
// <copyright file="ShellAssignmentDigestFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Netclaw.Configuration;
using ShellSyntaxTree;

namespace Netclaw.Security;

internal static class ShellAssignmentDigestFactory
{
    private const uint FormatVersion = 1;
    private static readonly byte[] Magic = "NCAS"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryCreate(
        ApprovalShell shell,
        IReadOnlyList<ShellVariableAssignment> assignments,
        out ApprovalAssignmentDigest? digest)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        digest = null;
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

        digest = new ApprovalAssignmentDigest(
            $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}");
        return true;
    }

    /// <summary>
    /// Returns the assignments that can reach the environment of a program:
    /// each assignment except a Bash shell-state assignment that the parser
    /// proves stays in the shell (owner decision F3).
    /// </summary>
    /// <remarks>
    /// Use it only for a program that is not a Bash data command. A data command
    /// is a builtin that reads no environment, and its digest guards operands
    /// that are not proved data, so it keeps every assignment.
    /// </remarks>
    public static IReadOnlyList<ShellVariableAssignment> ReachingProgram(
        ApprovalShell shell,
        IReadOnlyList<ShellVariableAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        return assignments.Where(assignment => !StaysInShell(shell, assignment)).ToArray();
    }

    /// <summary>
    /// Returns true when the parser proves that a Bash shell-state assignment
    /// reaches no child process (owner decision F3).
    /// </summary>
    /// <remarks>
    /// SECURITY: ShellSyntaxTree 0.4.0-beta.24 sets
    /// <see cref="ShellVariableAssignment.MayAffectProcessEnvironment"/> to false
    /// only under a fresh no-startup Bash state, with the complete launch
    /// environment names, when no path to the command exports the name and the
    /// name is not in the launch environment. Bash then passes the variable to
    /// no program, so the variable cannot change what the program does. A read
    /// of the variable as a word is an argument with its own value facts. Every
    /// other assignment keeps its place in the digest: a command prefix
    /// (<c>X=1 cmd</c>), a PowerShell assignment, and a Bash assignment that can
    /// reach the environment (<c>GIT_DIR=/x; git status</c> when the launch
    /// environment holds <c>GIT_DIR</c>).
    /// </remarks>
    private static bool StaysInShell(ApprovalShell shell, ShellVariableAssignment? assignment)
        => shell == ApprovalShell.Bash
           && assignment is
           {
               Scope: ShellVariableAssignmentScope.ShellState,
               MayAffectProcessEnvironment: false
           };

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
