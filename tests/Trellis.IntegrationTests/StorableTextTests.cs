using Shouldly;
using Trellis.Persistence.Outbox;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// What the outbox writes into a row's <c>Error</c> is made storable first: each UTF-16 surrogate that is not half
/// of a valid pair, and each NUL, becomes U+FFFD, and nothing else changes. Pure, so pinned here without a
/// database; <c>OutboxRetryTests</c> books such a failure against the real one.
/// </summary>
public sealed class StorableTextTests
{
    /// <summary>
    /// Keyed by the name a theory row passes: the runner hands a row its arguments through UTF-8, which would turn
    /// a lone surrogate into U+FFFD before the function saw it.
    /// </summary>
    private static readonly Dictionary<string, (string Text, string Storable)> Rows = new(StringComparer.Ordinal)
    {
        ["a lone high surrogate"] = ("smtp \uD83D down", "smtp � down"),
        ["a lone low surrogate"] = ("smtp \uDE00 down", "smtp � down"),
        ["a high surrogate that ends the text"] = ("smtp down \uD83D", "smtp down �"),
        ["a low surrogate before a high one"] = ("\uDE00\uD83D", "��"),
        ["a high surrogate before a valid pair"] = ("\uD83D😀", "�😀"),
        ["a valid pair, kept"] = ("smtp 😀 down", "smtp 😀 down"),
        ["a NUL"] = ("smtp \0 down", "smtp � down"),
        ["empty"] = ("", ""),
    };

    public static TheoryData<string> RowNames => [.. Rows.Keys];

    [Theory]
    [MemberData(nameof(RowNames))]
    public void Each_refused_code_unit_becomes_U_FFFD_and_nothing_else_changes(string row)
    {
        (string text, string storable) = Rows[row];

        StorableText.From(text).ShouldBe(storable);
    }

    [Fact]
    public void Text_with_nothing_to_replace_is_returned_as_the_same_instance()
    {
        string text = "SMTP submission to j***@example.com failed: RecipientNotAccepted";

        StorableText.From(text).ShouldBeSameAs(text);
    }
}
