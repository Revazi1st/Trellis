using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// Positive controls for the comment stripper every source scan reads through: a stripper that ate code
/// would make the zero-expected scans pass on anything, and one that kept a comment would let prose that
/// names a forbidden call trip them.
/// </summary>
public sealed class RepositorySourceTests
{
    [Theory]
    [InlineData("var url = \"https://example.com/a\"; // marker()", "var url = \"https://example.com/a\"; ")]
    [InlineData("var path = @\"C:\\temp\\\"; // marker()", "var path = @\"C:\\temp\\\"; ")]
    [InlineData("char quote = '\"'; // marker()", "char quote = '\"'; ")]
    [InlineData("call(/* marker() */ value);", "call(           value);")]
    public void Comments_go_and_literals_stay(string source, string expected)
        => RepositorySource.StripComments(source).ShouldBe(expected);

    [Fact]
    public void A_block_comment_keeps_its_line_breaks_so_positions_still_map_to_lines()
    {
        string stripped = RepositorySource.StripComments("first();\n/* one\ntwo */\nmarker();");

        new SourceFile("File.cs", stripped).LineOf(stripped.IndexOf("marker", StringComparison.Ordinal)).ShouldBe(4);
        stripped.ShouldNotContain("one", Case.Sensitive);
    }
}
