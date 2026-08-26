// -----------------------------------------------------------------------
// <copyright file="ToolArgumentHelperStrictTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Strict argument helpers must distinguish absent (→ null), parseable
/// (→ value), and present-but-invalid (→ ArgumentException) — the silent
/// coercion to 0/0.0/false that the non-strict helpers perform is the
/// defect class these exist to close (tool-arg-validation spec).
/// </summary>
public class ToolArgumentHelperStrictTests
{
    private static Dictionary<string, object?> Args(string key, object? value)
        => new() { [key] = value };

    private static JsonElement Json(string raw)
        => JsonDocument.Parse(raw).RootElement.Clone();

    // ── GetIntStrict: absent vs invalid ──

    [Fact]
    public void IntStrict_absent_key_returns_null()
    {
        Assert.Null(ToolArgumentHelper.GetIntStrict(new Dictionary<string, object?>(), "Limit"));
        Assert.Null(ToolArgumentHelper.GetIntStrict(null, "Limit"));
    }

    [Fact]
    public void IntStrict_json_null_value_treated_as_absent()
    {
        Assert.Null(ToolArgumentHelper.GetIntStrict(Args("Limit", Json("null")), "Limit"));
        Assert.Null(ToolArgumentHelper.GetIntStrict(Args("Limit", null), "Limit"));
    }

    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    [InlineData(-7)]
    public void IntStrict_native_int_parses(int value)
    {
        Assert.Equal(value, ToolArgumentHelper.GetIntStrict(Args("Limit", value), "Limit"));
    }

    [Fact]
    public void IntStrict_long_in_range_parses()
    {
        Assert.Equal(500, ToolArgumentHelper.GetIntStrict(Args("Limit", 500L), "Limit"));
    }

    [Fact]
    public void IntStrict_long_out_of_range_throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ToolArgumentHelper.GetIntStrict(Args("Limit", long.MaxValue), "Limit"));
        Assert.Contains("Limit", ex.Message);
        Assert.Contains("integer", ex.Message);
    }

    [Fact]
    public void IntStrict_integral_double_parses()
    {
        Assert.Equal(12, ToolArgumentHelper.GetIntStrict(Args("Limit", 12.0), "Limit"));
    }

    [Fact]
    public void IntStrict_non_integral_double_throws_no_silent_truncation()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ToolArgumentHelper.GetIntStrict(Args("Limit", 12.7), "Limit"));
        Assert.Contains("12.7", ex.Message);
    }

    [Fact]
    public void IntStrict_json_number_parses()
    {
        Assert.Equal(300, ToolArgumentHelper.GetIntStrict(Args("Limit", Json("300")), "Limit"));
    }

    [Fact]
    public void IntStrict_json_integral_with_decimal_point_accepted()
    {
        // Models commonly emit whole numbers as 300.0 — JsonElement.TryGetInt32
        // rejects that text, so it must fall through to the integral-double
        // check rather than being rejected as invalid.
        Assert.Equal(300, ToolArgumentHelper.GetIntStrict(Args("Limit", Json("300.0")), "Limit"));
        Assert.Equal(12, ToolArgumentHelper.GetIntStrict(Args("Limit", Json("12.0")), "Limit"));
    }

    [Fact]
    public void IntStrict_json_fractional_still_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => ToolArgumentHelper.GetIntStrict(Args("Limit", Json("12.5")), "Limit"));
    }

    [Fact]
    public void IntStrict_json_non_integral_number_throws_not_uncaught()
    {
        // The non-strict path called JsonElement.GetInt32() which throws
        // FormatException uncaught; strict must convert to ArgumentException.
        var ex = Assert.Throws<ArgumentException>(
            () => ToolArgumentHelper.GetIntStrict(Args("Limit", Json("12.5")), "Limit"));
        Assert.Contains("Limit", ex.Message);
    }

    [Fact]
    public void IntStrict_json_overflow_number_throws_argument_exception()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ToolArgumentHelper.GetIntStrict(Args("Limit", Json("99999999999")), "Limit"));
        Assert.Contains("integer", ex.Message);
    }

    [Fact]
    public void IntStrict_numeric_string_parses()
    {
        Assert.Equal(1200, ToolArgumentHelper.GetIntStrict(Args("Limit", "1200"), "Limit"));
        Assert.Equal(1200, ToolArgumentHelper.GetIntStrict(Args("Limit", Json("\"1200\"")), "Limit"));
    }

    [Fact]
    public void IntStrict_unparseable_string_throws_naming_param_value_type()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ToolArgumentHelper.GetIntStrict(Args("Limit", "abc"), "Limit"));
        Assert.Contains("'Limit'", ex.Message);
        Assert.Contains("'abc'", ex.Message);
        Assert.Contains("integer", ex.Message);
    }

    [Fact]
    public void IntStrict_flexible_key_matching_preserved()
    {
        // Existing deterministic canonicalization (case/punctuation) must keep
        // working — strictness applies to values, not key matching.
        Assert.Equal(5, ToolArgumentHelper.GetIntStrict(Args("limit", 5), "Limit"));
        Assert.Equal(5, ToolArgumentHelper.GetIntStrict(Args("start_line", 5), "StartLine"));
    }

    // ── GetDoubleStrict ──

    [Fact]
    public void DoubleStrict_absent_returns_null_valid_parses_invalid_throws()
    {
        Assert.Null(ToolArgumentHelper.GetDoubleStrict(new Dictionary<string, object?>(), "Scale"));
        Assert.Equal(2.5, ToolArgumentHelper.GetDoubleStrict(Args("Scale", 2.5), "Scale"));
        Assert.Equal(2.5, ToolArgumentHelper.GetDoubleStrict(Args("Scale", Json("2.5")), "Scale"));
        Assert.Equal(2.5, ToolArgumentHelper.GetDoubleStrict(Args("Scale", "2.5"), "Scale"));
        Assert.Equal(3.0, ToolArgumentHelper.GetDoubleStrict(Args("Scale", 3), "Scale"));

        var ex = Assert.Throws<ArgumentException>(
            () => ToolArgumentHelper.GetDoubleStrict(Args("Scale", "fast"), "Scale"));
        Assert.Contains("'Scale'", ex.Message);
        Assert.Contains("number", ex.Message);
    }

    // ── GetBoolStrict ──

    [Fact]
    public void BoolStrict_absent_returns_null()
    {
        Assert.Null(ToolArgumentHelper.GetBoolStrict(new Dictionary<string, object?>(), "Recursive"));
    }

    [Fact]
    public void BoolStrict_valid_forms_parse()
    {
        Assert.True(ToolArgumentHelper.GetBoolStrict(Args("Recursive", true), "Recursive"));
        Assert.True(ToolArgumentHelper.GetBoolStrict(Args("Recursive", Json("true")), "Recursive"));
        Assert.False(ToolArgumentHelper.GetBoolStrict(Args("Recursive", Json("false")), "Recursive"));
        Assert.True(ToolArgumentHelper.GetBoolStrict(Args("Recursive", "true"), "Recursive"));
        Assert.True(ToolArgumentHelper.GetBoolStrict(Args("Recursive", "True"), "Recursive"));
        Assert.True(ToolArgumentHelper.GetBoolStrict(Args("Recursive", Json("\"true\"")), "Recursive"));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    public void BoolStrict_colloquial_string_throws(string value)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ToolArgumentHelper.GetBoolStrict(Args("Recursive", value), "Recursive"));
        Assert.Contains("'Recursive'", ex.Message);
        Assert.Contains("boolean", ex.Message);
    }

    [Fact]
    public void BoolStrict_numeric_one_throws()
    {
        Assert.Throws<ArgumentException>(
            () => ToolArgumentHelper.GetBoolStrict(Args("Recursive", 1), "Recursive"));
    }

    [Fact]
    public void Strict_error_renders_long_values_bounded()
    {
        var huge = new string('x', 500);
        var ex = Assert.Throws<ArgumentException>(
            () => ToolArgumentHelper.GetIntStrict(Args("Limit", huge), "Limit"));
        Assert.True(ex.Message.Length < 300);
    }

    [Fact]
    public void Strict_string_parsing_is_culture_invariant()
    {
        // The daemon does not run with InvariantGlobalization, so a comma-decimal
        // host locale must not change how a string-typed numeric argument parses.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(1.5, ToolArgumentHelper.GetDoubleStrict(Args("Scale", "1.5"), "Scale"));
            Assert.Equal(1500, ToolArgumentHelper.GetIntStrict(Args("Limit", "1500"), "Limit"));
            // de-DE would read "1.5" as 15 via a group separator if culture leaked in.
            Assert.NotEqual(15.0, ToolArgumentHelper.GetDoubleStrict(Args("Scale", "1.5"), "Scale"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void StringDictionary_reads_json_object_with_string_values()
    {
        var result = ToolArgumentHelper.GetStringDictionary(
            Args("Arguments", Json("""{"property":"petabridge-com","monthsBack":"1"}""")),
            "Arguments");

        Assert.NotNull(result);
        Assert.Equal("petabridge-com", result["property"]);
        Assert.Equal("1", result["monthsBack"]);
    }

    [Fact]
    public void StringDictionary_rejects_non_string_values()
    {
        var error = Assert.Throws<ArgumentException>(() => ToolArgumentHelper.GetStringDictionary(
            Args("Arguments", Json("""{"monthsBack":1}""")),
            "Arguments"));

        Assert.Contains("Arguments.monthsBack", error.Message);
    }

    [Fact]
    public void StringArray_reads_json_and_clr_arrays_without_coercion()
    {
        var pointers = ToolArgumentHelper.GetStringArray(
            Args("Pointers", Json("""["/status","/items/0/name"]""")),
            "Pointers");
        var paths = ToolArgumentHelper.GetStringArray(
            Args("Paths", new[] { "a.txt", "b.txt" }),
            "Paths");

        Assert.NotNull(pointers);
        Assert.Equal(
            ["/status", "/items/0/name"],
            pointers);
        Assert.NotNull(paths);
        Assert.Equal(
            ["a.txt", "b.txt"],
            paths);
    }

    [Fact]
    public void StringArray_rejects_scalar_and_non_string_members()
    {
        Assert.Throws<ArgumentException>(() => ToolArgumentHelper.GetStringArray(
            Args("Paths", "a.txt"),
            "Paths"));
        var error = Assert.Throws<ArgumentException>(() => ToolArgumentHelper.GetStringArray(
            Args("Paths", Json("""["a.txt",1]""")),
            "Paths"));
        Assert.Contains("Paths[1]", error.Message, StringComparison.Ordinal);
    }
}
