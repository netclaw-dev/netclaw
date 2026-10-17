// -----------------------------------------------------------------------
// <copyright file="DoctorExitCodeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class DoctorExitCodeTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void A_failed_fix_makes_the_exit_code_1(int checkExitCode, int expected)
        => Assert.Equal(expected, new DoctorRunResult([], checkExitCode).ExitCodeAfterFix(fixFailed: true));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Without_a_failed_fix_the_exit_code_is_the_one_of_the_checks(int checkExitCode)
        => Assert.Equal(checkExitCode, new DoctorRunResult([], checkExitCode).ExitCodeAfterFix(fixFailed: false));
}
