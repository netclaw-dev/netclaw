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

    [Fact]
    public void NormalizePlural_prices_to_price()
    {
        Assert.Equal("price", TextTokenizer.NormalizePlural("prices"));
    }

    [Fact]
    public void NormalizePlural_flights_to_flight()
    {
        Assert.Equal("flight", TextTokenizer.NormalizePlural("flights"));
    }

    [Fact]
    public void NormalizePlural_categories_to_category()
    {
        Assert.Equal("category", TextTokenizer.NormalizePlural("categories"));
    }

    [Fact]
    public void NormalizePlural_matches_to_match()
    {
        Assert.Equal("match", TextTokenizer.NormalizePlural("matches"));
    }

    [Fact]
    public void NormalizePlural_buses_to_bus()
    {
        Assert.Equal("bus", TextTokenizer.NormalizePlural("buses"));
    }

    [Fact]
    public void NormalizePlural_class_stays_class()
    {
        Assert.Equal("class", TextTokenizer.NormalizePlural("class"));
    }

    [Fact]
    public void NormalizePlural_miss_stays_miss()
    {
        Assert.Equal("miss", TextTokenizer.NormalizePlural("miss"));
    }

    [Fact]
    public void NormalizePlural_short_words_unchanged()
    {
        Assert.Equal("us", TextTokenizer.NormalizePlural("us"));
        Assert.Equal("has", TextTokenizer.NormalizePlural("has"));
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
