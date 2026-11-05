using SnomedSearch.Infrastructure.Data;
using Xunit;

namespace SnomedSearch.Tests;

/// <summary>Pure unit tests, no database.</summary>
public class TokenizerTests
{
    [Theory]
    [InlineData("Chest  Pain", new[] { "chest", "pain" })]
    [InlineData("x-ray, left/right", new[] { "x", "ray", "left", "right" })]
    [InlineData("100%_off", new[] { "100", "off" })]
    [InlineData("% _ \\", new string[0])]
    [InlineData("", new string[0])]
    public void Tokenize_keeps_letters_and_digits_only(string input, string[] expected) =>
        Assert.Equal(expected, LexicalSearch.Tokenize(input));

    [Fact]
    public void Tokenize_caps_the_number_of_words() =>
        Assert.Equal(3, LexicalSearch.Tokenize("a b c d e f", maxWords: 3).Count);

    [Fact]
    public void Tokenize_never_emits_tsquery_syntax()
    {
        // Operators a user could type to change the tsquery must disappear.
        var tokens = LexicalSearch.Tokenize("pain & !fever | (a <-> b) :*");
        Assert.All(tokens, t => Assert.Matches(@"^[\p{L}\p{N}]+$", t));
    }

    [Theory]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("back\\slash", "back\\\\slash")]
    public void EscapeLike_escapes_wildcards(string input, string expected) =>
        Assert.Equal(expected, LexicalSearch.EscapeLike(input));
}
