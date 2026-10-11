using System.Text;
using System.Text.RegularExpressions;

namespace Trellis.ArchitectureTests;

/// <summary>
/// Reads values out of comment-stripped source (<see cref="RepositorySource"/>) for the rules that need
/// what a call passes, not only that it happens (<see cref="TelemetryNamingTests"/>,
/// <see cref="ErrorCodeVocabularyTests"/>): a call's top-level arguments, and the string an argument stands
/// for. That is a literal, or a <c>const string</c> declared in the same file or, for <c>Type.Name</c>, in
/// <c>Type.cs</c>. Anything else (a computed value, a constant declared elsewhere) does not resolve, and
/// the rule decides what that means.
/// </summary>
internal sealed partial class SourceConstants
{
    private readonly Dictionary<string, SourceFile> _byFileName;

    public SourceConstants(IEnumerable<SourceFile> files)
        => _byFileName = files
            .GroupBy(static f => Path.GetFileNameWithoutExtension(f.RelativePath), StringComparer.Ordinal)
            .Where(static g => g.Count() == 1)
            .ToDictionary(static g => g.Key, static g => g.Single(), StringComparer.Ordinal);

    /// <summary>The string <paramref name="argument"/> (as written in <paramref name="file"/>) stands for, or <see langword="null"/>.</summary>
    public string? Resolve(SourceFile file, string argument)
    {
        if (StringLiteral().Match(argument) is { Success: true } literal)
        {
            return literal.Groups["value"].Value;
        }

        if (!QualifiedIdentifier().IsMatch(argument))
        {
            return null;
        }

        string[] parts = argument.Split('.');
        return ConstantIn(file, parts[^1])
               ?? (parts.Length > 1 && _byFileName.TryGetValue(parts[^2], out SourceFile? declaring)
                   ? ConstantIn(declaring, parts[^1])
                   : null);
    }

    /// <summary>The top-level arguments of the call whose opening parenthesis is at <paramref name="open"/>, trimmed.</summary>
    public static List<string> ArgumentsAt(string code, int open)
    {
        List<string> arguments = [];
        var current = new StringBuilder();
        int depth = 0;
        int i = open;
        while (i < code.Length)
        {
            char c = code[i];
            if (c == '"')
            {
                // A string literal is copied whole: its commas and parentheses are not syntax.
                int end = i + 1;
                while (end < code.Length && code[end] != '"')
                {
                    end += code[end] == '\\' ? 2 : 1;
                }

                end = Math.Min(end, code.Length - 1);
                current.Append(code, i, end - i + 1);
                i = end + 1;
                continue;
            }

            i++;
            if (c is '(' or '[' or '{')
            {
                depth++;
                if (depth == 1)
                {
                    continue;
                }
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }
            else if (c == ',' && depth == 1)
            {
                arguments.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        arguments.Add(current.ToString().Trim());
        return arguments;
    }

    private static string? ConstantIn(SourceFile file, string identifier)
        => ConstantDeclaration().Matches(file.Code)
            .FirstOrDefault(m => string.Equals(m.Groups["name"].Value, identifier, StringComparison.Ordinal))
            ?.Groups["value"].Value;

    [GeneratedRegex("""\bconst\s+string\s+(?<name>\w+)\s*=\s*"(?<value>(?:[^"\\]|\\.)*)"\s*;""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConstantDeclaration();

    [GeneratedRegex("""^"(?<value>(?:[^"\\]|\\.)*)"$""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QualifiedIdentifier();
}
