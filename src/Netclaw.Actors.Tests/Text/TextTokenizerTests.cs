// -----------------------------------------------------------------------
// <copyright file="TextTokenizerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Text;
using Xunit;

namespace Netclaw.Actors.Tests.Text;

public class TextTokenizerTests
{
    [Fact]
    public void Tokenize_strips_stopwords()
    {
        var tokens = TextTokenizer.Tokenize("I need to buy a thing");

        Assert.Contains("need", tokens);
        Assert.Contains("buy", tokens);
        Assert.Contains("thing", tokens);
        Assert.DoesNotContain("i", tokens);
        Assert.DoesNotContain("to", tokens);
        Assert.DoesNotContain("a", tokens);
    }

    [Fact]
    public void Tokenize_lowercases_tokens()
    {
        var tokens = TextTokenizer.Tokenize("BUY Price FLIGHT");

        Assert.Contains("buy", tokens);
        Assert.Contains("price", tokens);
        Assert.Contains("flight", tokens);
    }

    [Fact]
    public void Tokenize_drops_single_char_tokens()
    {
        var tokens = TextTokenizer.Tokenize("I x am");

        Assert.DoesNotContain("x", tokens);
    }

    [Fact]
    public void Tokenize_preserves_hyphens()
    {
        var tokens = TextTokenizer.Tokenize("a 2-keg regulator");

        Assert.Contains("2-keg", tokens);
        Assert.Contains("regulator", tokens);
    }

    [Fact]
    public void Tokenize_normalizes_plurals()
    {
        var tokens = TextTokenizer.Tokenize("prices flights categories");

        Assert.Contains("price", tokens);
        Assert.Contains("flight", tokens);
        Assert.Contains("category", tokens);
    }

    [Theory]
    [InlineData("prices", "price")]
    [InlineData("flights", "flight")]
    [InlineData("categories", "category")]
    [InlineData("matches", "match")]
    [InlineData("buses", "bus")]
    [InlineData("class", "class")]
    [InlineData("miss", "miss")]
    [InlineData("us", "us")]
    [InlineData("has", "has")]
    public void NormalizePlural_produces_expected_singular(string input, string expected)
    {
        Assert.Equal(expected, TextTokenizer.NormalizePlural(input));
    }

    [Fact]
    public void MakeBigrams_consecutive_pairs()
    {
        var tokens = new List<string> { "co2", "regulator", "value" };
        var bigrams = TextTokenizer.MakeBigrams(tokens);

        Assert.Equal(2, bigrams.Count);
        Assert.Equal("co2 regulator", bigrams[0]);
        Assert.Equal("regulator value", bigrams[1]);
    }

    [Fact]
    public void MakeBigrams_single_token_returns_empty()
    {
        var bigrams = TextTokenizer.MakeBigrams(new List<string> { "solo" });

        Assert.Empty(bigrams);
    }

    [Fact]
    public void MakeBigrams_empty_returns_empty()
    {
        var bigrams = TextTokenizer.MakeBigrams(new List<string>());

        Assert.Empty(bigrams);
    }
}
