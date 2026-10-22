// -----------------------------------------------------------------------
// <copyright file="ToolCallLogFormatterHardeningTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// Review findings for issue #2589. The tool call log line leaves the host, so
/// the formatter must fail closed and must never throw.
/// <list type="bullet">
/// <item>A secret inside a JSON text in a string value must be redacted.</item>
/// <item>A value of an unknown type becomes "&lt;TypeName&gt;". The formatter does
/// not serialize it.</item>
/// <item>A bad value must not throw and must not stop the tool call. A total
/// failure gives exactly "&lt;arguments unavailable&gt;".</item>
/// <item>A long string that the redactor changes gets no hash, because a reader
/// can brute-force a known template from the hash.</item>
/// </list>
/// </summary>
public sealed class ToolCallLogFormatterHardeningTests
{
    private const string Redacted = "***REDACTED***";
    private const string Unavailable = "<arguments unavailable>";

    // Finding 1: the redactor ran only on the serialized text, where the inner
    // quotes are escaped as \". Its JSON rule then cannot match.

    [Fact]
    public void Secret_inside_a_json_text_in_a_string_value_is_redacted()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["content"] = "{\"token\": \"abc123def\"}"
        };

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.DoesNotContain("abc123def", text, StringComparison.Ordinal);
        Assert.Contains(Redacted, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_inside_a_json_body_in_a_command_is_redacted()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["command"] = "curl -d '{\"password\":\"hunter2\"}' https://x"
        };

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("curl -d", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_inside_a_json_text_in_a_parsed_body_value_is_redacted()
    {
        // The model sends arguments as JSON, so the body is a JsonElement string.
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            """{"body":"{\"password\":\"hunter2\"}"}""")!;

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains(Redacted, text, StringComparison.Ordinal);
    }

    // Finding 2: an unknown type went through JsonSerializer. The SensitiveString
    // converter writes the raw value, and a JsonObject is not walked.

    [Fact]
    public void Json_object_value_becomes_its_type_name()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["opts"] = JsonNode.Parse("""{"password":"hunter2","x":1}""")
        };

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("\"<JsonObject>\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Object_with_a_sensitive_string_property_becomes_its_type_name()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["holder"] = new SensitiveHolder(new SensitiveString("hunter2"))
        };

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("\"<SensitiveHolder>\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_object_becomes_exactly_its_short_type_name()
    {
        var arguments = new Dictionary<string, object?> { ["poco"] = new CustomPoco() };

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.Equal("{\"poco\":\"<CustomPoco>\"}", text);
    }

    [Fact]
    public void Allowed_scalar_and_collection_types_keep_their_values()
    {
        // Positive control for the fail-closed rule: the allowed types still show.
        var arguments = new Dictionary<string, object?>
        {
            ["count"] = 42,
            ["size"] = 9_000_000_000L,
            ["price"] = 12.5m,
            ["ratio"] = 1.25d,
            ["dryRun"] = true,
            ["nothing"] = null,
            ["items"] = new List<object?> { "first-item", 7 },
            ["options"] = new Dictionary<string, object?> { ["mode"] = "fast" }
        };

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        foreach (var expected in new[] { "42", "9000000000", "12.5", "1.25", "true", "null", "first-item", "7", "fast" })
            Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    // Finding 3: the formatter must never throw. The log line comes before the
    // tool runs, so an exception here fails the tool call.

    [Fact]
    public void Default_json_element_does_not_throw()
    {
        var arguments = new Dictionary<string, object?> { ["e"] = default(JsonElement) };

        var text = FormatWithoutThrow(arguments);

        Assert.NotEmpty(text);
    }

    [Fact]
    public void Object_with_a_throwing_getter_becomes_its_type_name()
    {
        var arguments = new Dictionary<string, object?> { ["o"] = new ThrowingGetterPoco() };

        var text = FormatWithoutThrow(arguments);

        Assert.Contains("\"<ThrowingGetterPoco>\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_a_number_double_becomes_its_type_name()
    {
        var arguments = new Dictionary<string, object?> { ["d"] = double.NaN };

        var text = FormatWithoutThrow(arguments);

        Assert.Contains("\"<Double>\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Dictionary_with_a_throwing_key_does_not_throw_or_leak()
    {
        // A Hashtable is not an allowed type, so it becomes its type name and its
        // keys are not read.
        var arguments = new Dictionary<string, object?>
        {
            ["h"] = new Hashtable { [new ThrowingKey()] = "hashtable-raw-value" }
        };

        var text = FormatWithoutThrow(arguments);

        Assert.DoesNotContain("hashtable-raw-value", text, StringComparison.Ordinal);
        Assert.Contains("\"<Hashtable>\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void List_with_a_throwing_enumerator_does_not_throw_or_leak()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["items"] = new ThrowingList(),
            ["mode"] = "fast"
        };

        var text = FormatWithoutThrow(arguments);

        Assert.DoesNotContain(ThrowingList.RawItem, text, StringComparison.Ordinal);
        Assert.NotEmpty(text);
    }

    [Fact]
    public void Arguments_that_throw_on_every_read_give_the_unavailable_marker()
    {
        var text = FormatWithoutThrow(new ThrowingArguments());

        Assert.Equal(Unavailable, text);
    }

    [Fact]
    public async Task List_that_holds_itself_twice_finishes_fast()
    {
        // Each level doubles the work. A depth limit of 64 alone gives 2^64 nodes.
        var list = new List<object?>();
        list.Add(list);
        list.Add(list);
        var arguments = new Dictionary<string, object?> { ["l"] = list };

        var format = Task.Run(() => ToolCallLogFormatter.FormatArguments(arguments), TestContext.Current.CancellationToken);
        var text = await format.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.NotEmpty(text);
        Assert.True(
            text.Length <= ToolCallLogFormatter.MaxArgumentsChars + 3,
            $"The arguments text has {text.Length} chars.");
    }

    // Finding 4: a hash of a long string that held a secret lets a reader confirm
    // a guessed secret when the rest of the text is a known template.

    [Fact]
    public void Long_string_that_the_redactor_changes_gets_no_hash()
    {
        const string prefix = "{\"Password\":\"Summer2024!\",\"note\":\"";
        const string suffix = "\"}";
        var config = prefix + new string('n', 300 - prefix.Length - suffix.Length) + suffix;
        Assert.Equal(300, config.Length);
        var arguments = new Dictionary<string, object?> { ["config"] = config };

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.Contains("<300 chars, redacted>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Summer2024!", text, StringComparison.Ordinal);
    }

    private static string FormatWithoutThrow(IDictionary<string, object?> arguments)
    {
        var text = default(string);
        var exception = Record.Exception(() => text = ToolCallLogFormatter.FormatArguments(arguments));
        Assert.Null(exception);
        Assert.NotNull(text);
        return text;
    }

    private sealed record SensitiveHolder(SensitiveString Value);

    private sealed class CustomPoco
    {
        public string Text { get; } = "visible-poco-text";
    }

    private sealed class ThrowingGetterPoco
    {
        private readonly string _message = "The getter throws.";

        public string Value => throw new InvalidOperationException(_message);
    }

    private sealed class ThrowingKey
    {
        public override string ToString() => throw new FormatException("The key throws.");
    }

    /// <summary>
    /// An allowed IList. Each way to read its items gives the first item or
    /// throws, so a walk that reads the items cannot avoid the exception.
    /// </summary>
    private sealed class ThrowingList : ArrayList
    {
        public const string RawItem = "enumerated-raw-item";

        public ThrowingList()
        {
            base.Add(RawItem);
            base.Add("second-raw-item");
        }

        public override IEnumerator GetEnumerator()
        {
            yield return RawItem;
            throw new InvalidOperationException("The enumerator throws.");
        }

        public override object? this[int index]
        {
            get => index == 0 ? RawItem : throw new InvalidOperationException("The indexer throws.");
            set => throw new NotSupportedException();
        }

        public override void CopyTo(Array array, int arrayIndex) => throw new InvalidOperationException("CopyTo throws.");

        public override object?[] ToArray() => throw new InvalidOperationException("ToArray throws.");
    }

    /// <summary>Arguments that throw on every read.</summary>
    private sealed class ThrowingArguments : IDictionary<string, object?>
    {
        private static InvalidOperationException Fail() => new("The arguments throw.");

        public object? this[string key] { get => throw Fail(); set => throw Fail(); }
        public ICollection<string> Keys => throw Fail();
        public ICollection<object?> Values => throw Fail();
        public int Count => throw Fail();
        public bool IsReadOnly => true;
        public void Add(string key, object? value) => throw Fail();
        public void Add(KeyValuePair<string, object?> item) => throw Fail();
        public void Clear() => throw Fail();
        public bool Contains(KeyValuePair<string, object?> item) => throw Fail();
        public bool ContainsKey(string key) => throw Fail();
        public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex) => throw Fail();
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw Fail();
        public bool Remove(string key) => throw Fail();
        public bool Remove(KeyValuePair<string, object?> item) => throw Fail();
        public bool TryGetValue(string key, [MaybeNullWhen(false)] out object? value) => throw Fail();
        IEnumerator IEnumerable.GetEnumerator() => throw Fail();
    }
}
