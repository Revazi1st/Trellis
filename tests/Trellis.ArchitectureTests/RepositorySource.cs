using System.Text;

namespace Trellis.ArchitectureTests;

/// <summary>One source file (C#, Razor, CSS, MSBuild or analyzer configuration) under the repository root, its comments already removed.</summary>
internal sealed record SourceFile(string RelativePath, string Code)
{
    /// <summary>The 1-based line of <paramref name="index"/> in <see cref="Code"/> — for failure messages.</summary>
    public int LineOf(int index) => Code.AsSpan(0, index).Count('\n') + 1;

    /// <summary><c>path:line: text</c> for the hit at <paramref name="index"/>, the line's text trimmed: one row of a failure message.</summary>
    public string Describe(int index)
    {
        int start = index == 0 ? 0 : Code.LastIndexOf('\n', index - 1) + 1;
        int end = Code.IndexOf('\n', index);
        return $"{RelativePath}:{LineOf(index)}: {Code[start..(end < 0 ? Code.Length : end)].Trim()}";
    }
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

    /// <summary>
    /// Every <c>*.razor</c> and <c>*.css</c> file under the given repository-relative directories
    /// (<c>src/Presentation/UI</c>, say), Razor, HTML, CSS and full-line C# comments stripped and line endings
    /// normalised (<see cref="StripMarkupComments"/>), so a scan reads the same text on every operating system
    /// and prose that names a colour or a forbidden call never trips a rule. A directory that does not exist
    /// is skipped.
    /// </summary>
    public static IEnumerable<SourceFile> RazorAndCssFiles(params string[] directories)
    {
        foreach (string relative in directories)
        {
            var directory = new DirectoryInfo(Path.Combine(Root.FullName, relative));
            if (!directory.Exists)
            {
                continue;
            }

            foreach (FileInfo file in directory.EnumerateFiles("*", SearchOption.AllDirectories)
                         .Where(static f => MarkupExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                         .Where(static f => !IsInSkippedDirectory(f))
                         .OrderBy(static f => f.FullName, StringComparer.Ordinal))
            {
                yield return new SourceFile(
                    Path.GetRelativePath(Root.FullName, file.FullName).Replace('\\', '/'),
                    StripMarkupComments(File.ReadAllText(file.FullName)));
            }
        }
    }

    private static readonly string[] MarkupExtensions = [".razor", ".css"];

    /// <summary>
    /// Every file under the given repository-relative directories, as its repository-relative path and its
    /// full path, in order, the build and tool folders skipped: for a scan that reads files of several kinds
    /// and strips each kind's comments itself (<see cref="StripXmlComments"/> for MSBuild files). A directory
    /// that does not exist is skipped.
    /// </summary>
    public static IEnumerable<(string RelativePath, string FullPath)> FilesUnder(params string[] directories)
    {
        foreach (string relative in directories)
        {
            var directory = new DirectoryInfo(Path.Combine(Root.FullName, relative));
            if (!directory.Exists)
            {
                continue;
            }

            foreach (FileInfo file in directory.EnumerateFiles("*", SearchOption.AllDirectories)
                         .Where(static f => !IsInSkippedDirectory(f))
                         .OrderBy(static f => f.FullName, StringComparer.Ordinal))
            {
                yield return (Path.GetRelativePath(Root.FullName, file.FullName).Replace('\\', '/'), file.FullName);
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
    internal static string StripComments(string source)
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

    /// <summary>
    /// Normalises line endings to <c>\n</c>, then removes Razor (<c>@* *@</c>), HTML (<c>&lt;!-- --&gt;</c>)
    /// and CSS (<c>/* */</c>) comments, and a C# line comment that fills its line (<c>//</c> or <c>///</c>
    /// after the indentation, as an <c>@code</c> block writes one), keeping line breaks so positions still map
    /// to lines. A <c>//</c> after other text on its line stays: it is also part of every address in the
    /// markup, and the C# stripper's string rules do not hold in markup text.
    /// </summary>
    internal static string StripMarkupComments(string source)
    {
        source = source.ReplaceLineEndings("\n");
        var code = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            if (i == 0 || source[i - 1] == '\n')
            {
                int first = i;
                while (first < source.Length && source[first] is ' ' or '\t')
                {
                    first++;
                }

                if (source.AsSpan(first).StartsWith("//", StringComparison.Ordinal))
                {
                    int lineEnd = source.IndexOf('\n', first);
                    int stopAt = lineEnd < 0 ? source.Length : lineEnd;
                    code.Append(' ', stopAt - i);
                    i = stopAt;
                    continue;
                }
            }

            ReadOnlySpan<char> rest = source.AsSpan(i);
            (string open, string close) = rest switch
            {
                _ when rest.StartsWith("@*", StringComparison.Ordinal) => ("@*", "*@"),
                _ when rest.StartsWith("<!--", StringComparison.Ordinal) => ("<!--", "-->"),
                _ when rest.StartsWith("/*", StringComparison.Ordinal) => ("/*", "*/"),
                _ => (string.Empty, string.Empty),
            };

            if (open.Length == 0)
            {
                code.Append(source[i]);
                i++;
                continue;
            }

            int end = source.IndexOf(close, i + open.Length, StringComparison.Ordinal);
            int stop = end < 0 ? source.Length : end + close.Length;
            for (int j = i; j < stop; j++)
            {
                code.Append(source[j] == '\n' ? '\n' : ' ');
            }

            i = stop;
        }

        return code.ToString();
    }

    /// <summary>
    /// Normalises line endings to <c>\n</c>, then removes XML comments (<c>&lt;!-- --&gt;</c>), keeping line
    /// breaks so positions still map to lines: for MSBuild files, whose globs (<c>**/*.cs</c>) the markup
    /// stripper would read as the start of a CSS comment.
    /// </summary>
    internal static string StripXmlComments(string source)
    {
        source = source.ReplaceLineEndings("\n");
        var code = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            int open = source.IndexOf("<!--", i, StringComparison.Ordinal);
            if (open < 0)
            {
                code.Append(source, i, source.Length - i);
                break;
            }

            code.Append(source, i, open - i);
            int close = source.IndexOf("-->", open + 4, StringComparison.Ordinal);
            int stop = close < 0 ? source.Length : close + 3;
            for (int j = open; j < stop; j++)
            {
                code.Append(source[j] == '\n' ? '\n' : ' ');
            }

            i = stop;
        }

        return code.ToString();
    }
}
