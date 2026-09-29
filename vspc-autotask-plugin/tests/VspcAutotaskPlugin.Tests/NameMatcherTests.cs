using VspcAutotaskPlugin.Sync;
using Xunit;

namespace VspcAutotaskPlugin.Tests;

public class NameMatcherTests
{
    [Theory]
    [InlineData("Contoso, Inc.", "contoso")]
    [InlineData("ACME LLC", "acme")]
    [InlineData("Fabrikam  Ltd", "fabrikam")]
    [InlineData("Northwind Traders GmbH", "northwind traders")]
    [InlineData("Tailspin Toys Co.", "tailspin toys")]
    [InlineData("  Wide World; Importers!  ", "wide world importers")]
    public void Normalize_strips_punctuation_case_and_legal_suffixes(string input, string expected) =>
        Assert.Equal(expected, NameMatcher.Normalize(input));

    [Fact]
    public void Normalize_keeps_single_token_even_if_it_is_a_suffix_word() =>
        Assert.Equal("co", NameMatcher.Normalize("Co"));

    [Fact]
    public void Normalize_empty_returns_empty()
    {
        Assert.Equal(string.Empty, NameMatcher.Normalize(""));
        Assert.Equal(string.Empty, NameMatcher.Normalize("   "));
    }

    [Fact]
    public void Similarity_is_one_for_names_equal_after_normalization() =>
        Assert.Equal(1.0, NameMatcher.Similarity("Contoso, Inc.", "CONTOSO"), 3);

    [Fact]
    public void Similarity_is_high_for_close_names()
    {
        Assert.True(NameMatcher.Similarity("Contoso", "Contosso") > 0.8);
        Assert.True(NameMatcher.Similarity("Northwind Traders", "Northwind Trader") > 0.9);
    }

    [Fact]
    public void Similarity_is_low_for_unrelated_names() =>
        Assert.True(NameMatcher.Similarity("Contoso", "Fabrikam") < 0.5);

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("abc", "abc", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    public void Levenshtein_computes_edit_distance(string a, string b, int expected) =>
        Assert.Equal(expected, NameMatcher.Levenshtein(a, b));
}
