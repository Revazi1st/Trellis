using System.Text;

namespace Trellis.ArchitectureTests;

/// <summary>One C# source file under the repository root, its comments already removed.</summary>
internal sealed record SourceFile(string RelativePath, string Code)
{
    /// <summary>The 1-based line of <paramref name="index"/> in <see cref="Code"/> — for failure messages.</summary>
    public int LineOf(int index) => Code.AsSpan(0, index).Count('\n') + 1;
}

/// <summary>
/// Source-scan support for fitness rules that reflection cannot see — the shape of a catch filter, the
/// overload a call site picks. Finds the repository root by walking up from the test binaries to the
/// directory holding the solution file, so it works the same in the template, in a generated project
/// and in the SDK container.
/// </summary>
internal static class RepositorySource
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", "artifacts", "node_modules", ".git"];

    private static readonly char[] DirectorySeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    public static DirectoryInfo Root { get; } = FindRoot();

    /// <summary>
    /// Every <c>*.cs</c> file under the given top-level directories (<c>src</c>, <c>tools</c>, …) with
    /// line and block comments stripped, so prose that NAMES a forbidden call never trips the rule.
    /// A directory a generated project does not have (no <c>tools</c>, say) is simply skipped.
    /// </summary>
    public static IEnumerable<SourceFile> CSharpFiles(params string[] topLevelDirectories)
    {
        foreach (string topLevel in topLevelDirectories)
        {
            var directory = new DirectoryInfo(Path.Combine(Root.FullName, topLevel));
            if (!directory.Exists)
            {
                continue;
            }

            foreach (FileInfo file in directory.EnumerateFiles("*.cs", SearchOption.AllDirectories)
                         .Where(static f => !IsInSkippedDirectory(f))
                         .OrderBy(static f => f.FullName, StringComparer.Ordinal))
            {
                yield return new SourceFile(
                    Path.GetRelativePath(Root.FullName, file.FullName).Replace('\\', '/'),
                    StripComments(File.ReadAllText(file.FullName)));
            }
        }
    }

    private static DirectoryInfo FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.slnx").Any())
            {
                return directory;
            }
        }

        throw new InvalidOperationException(
            $"No *.slnx above {AppContext.BaseDirectory}: the source-scan fitness tests need the repository checkout.");
    }

    private static bool IsInSkippedDirectory(FileInfo file)
        => file.DirectoryName!
            .Split(DirectorySeparators)
            .Any(static segment => SkippedDirectories.Contains(segment, StringComparer.Ordinal));

    /// <summary>
    /// Removes <c>//</c> and <c>/* */</c> comments, keeping line breaks so positions still map to lines.
    /// String and char literals are skipped over (a URL's <c>//</c> is not a comment); this is a scanner
    /// for fitness rules, not a C# lexer — raw and interpolated strings are treated as plain strings.
    /// </summary>
    private static string StripComments(string source)
    {
        var code = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && next == '*')
            {
                i += 2;
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                {
                    code.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                i += 2;
            }
            else if (c is '"' or '\'')
            {
                bool verbatim = i > 0 && source[i - 1] == '@';
                code.Append(c);
                i++;
                while (i < source.Length && source[i] != c && (verbatim || source[i] != '\n'))
                {
                    if (source[i] == '\\' && !verbatim && i + 1 < source.Length)
                    {
                        code.Append(source[i]);
                        i++;
                    }

                    code.Append(source[i]);
                    i++;
                }

                if (i < source.Length)
                {
                    code.Append(source[i]);
                    i++;
                }
            }
            else
            {
                code.Append(c);
                i++;
            }
        }

        return code.ToString();
    }
}
