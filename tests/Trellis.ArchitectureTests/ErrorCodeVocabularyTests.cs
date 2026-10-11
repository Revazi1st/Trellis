using System.Text.RegularExpressions;
using Shouldly;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// Error codes are the contract clients branch on (ADR-0024, .claude/rules/sharedkernel.md): <c>Area.Problem</c>,
/// PascalCase with one dot, and one meaning for good, so a code never answers 400 in one place and 500 in another
/// (<c>Auth.RegistrationFailed</c> did, until the registration fix gave the rejection its own code). A source scan
/// over <c>src</c> and <c>tools</c> reads every code the solution declares:
/// <list type="bullet">
/// <item>the first argument of every <c>Error.Validation/NotFound/…(</c> factory call, whose name is its <see cref="ErrorType"/>;</item>
/// <item>the code of every FluentValidation <c>.WithErrorCode(…)</c>, which the validation decorator turns into a
/// <see cref="ErrorType.Validation"/> entry;</item>
/// <item>the code of every <c>ProblemDescriptor</c> row (<c>ProblemCatalog</c> and its siblings), typed by its status.
/// The catalogue holds what is not a <c>Result</c>, so a catalogue code never doubles as an <c>Error</c>'s.</item>
/// </list>
/// A code is a literal or a <c>const string</c> (<see cref="SourceConstants"/>); a row may build a family from a
/// literal prefix (<c>"Http." + …</c>), which reserves the area. Only the SharedKernel derives codes at run time,
/// from its factories' parameters and FluentValidation's validator names, so a computed code anywhere else fails
/// as undeclared.
/// </summary>
public sealed partial class ErrorCodeVocabularyTests
{
    private static readonly Lazy<Vocabulary> Found = new(static () => Vocabulary.Read(RepositorySource.CSharpFiles("src", "tools")));

    [Fact]
    public void The_scan_reads_both_vocabularies()
    {
        // Both survive remove-feature and every toggle: the unit of work's conflict and the 401 catalogue row.
        Found.Value.Codes.ShouldContain(
            static site => site.Code == "Concurrency.Conflict" && site.Kind == nameof(ErrorType.Conflict),
            "the scan found no Error factory call; has the call shape changed?");
        Found.Value.Codes.ShouldContain(
            static site => site.Code == "Auth.Unauthenticated" && site.Kind == "HTTP 401",
            "the scan found no ProblemDescriptor row; has the row shape changed?");
    }

    [Fact]
    public void Codes_are_Area_dot_Problem_in_PascalCase()
        => Found.Value.MalformedCodes().ShouldBeEmpty(
            "a code is 'Area.Problem': PascalCase, one dot (Auth.EmailAlreadyInUse); a family prefix is 'Area.'");

    [Fact]
    public void A_code_keeps_one_meaning()
        => Found.Value.Collisions().ShouldBeEmpty(
            "clients branch on the code, so one code has one ErrorType (one status for a catalogue row): give the second meaning its own code");

    [Fact]
    public void Every_code_is_declared_where_the_scan_can_read_it()
        => Found.Value.Undeclared.ShouldBeEmpty(
            "pass a code as a string literal or a const string declared in the same file (or in Type.cs for Type.Name)");

    [Fact]
    public void The_scan_flags_one_code_with_two_meanings()
    {
        Vocabulary planted = Vocabulary.Read(
        [
            new SourceFile("src/Planted/IdentityService.cs", """
                private static readonly Error RoleAssignmentFailed = Error.Failure(
                    "Auth.RegistrationFailed",
                    "The account could not be set up.");
                """),
            new SourceFile("src/Planted/RegistrationErrors.cs", """
                public const string RejectedCode = "Auth.RegistrationFailed";
                public static Error From(string description) => SharedKernel.Results.Error.Validation(RejectedCode, description);
                """),
        ]);

        planted.Collisions().ShouldHaveSingleItem().ShouldStartWith("Auth.RegistrationFailed: Failure at src/Planted/IdentityService.cs:1");
    }

    [Theory]
    [InlineData("order.NotDraft")]
    [InlineData("Order.not_draft")]
    [InlineData("Order")]
    [InlineData("Order.Not.Draft")]
    [InlineData("")]
    public void The_scan_flags_a_malformed_code(string code)
        => Vocabulary.Read([new SourceFile("src/Planted/PlantedErrors.cs", $$"""
                public static readonly Error NotDraft = Error.Conflict("{{code}}", "Only a draft can change.");
                """)])
            .MalformedCodes()
            .ShouldHaveSingleItem();

    [Fact]
    public void The_scan_flags_a_validator_code_or_catalogue_row_that_reuses_an_error_code()
    {
        Vocabulary planted = Vocabulary.Read(
        [
            new SourceFile("src/Planted/OrderErrors.cs", """
                public static readonly Error NotDraft = Error.Conflict("Order.NotDraft", "Only a draft can change.");
                """),
            new SourceFile("src/Planted/AddOrderLineCommandValidator.cs", """
                RuleFor(c => c.OrderId).Must(BeADraft).WithErrorCode("Order.NotDraft");
                """),
            new SourceFile("src/Planted/ProblemConventions.cs", """
                public static readonly ProblemDescriptor Locked = new(
                    StatusCodes.Status423Locked,
                    "Order.NotDraft",
                    "Locked",
                    "The order is locked.");
                public static ProblemDescriptor ForStatus(int status) => new(status, "Http." + Suffix(status), "Failed", Detail: null);
                """),
            new SourceFile("src/Planted/HttpErrors.cs", """
                public static readonly Error Gone = Error.NotFound("Http.Gone", "It is gone.");
                """),
        ]);

        string[] collisions = [.. planted.Collisions()];

        collisions.Length.ShouldBe(2);
        collisions[0].ShouldStartWith("Order.NotDraft: Conflict at src/Planted/OrderErrors.cs:1, Validation at src/Planted/AddOrderLineCommandValidator.cs:1, HTTP 423");
        collisions[1].ShouldStartWith("Http.Gone: NotFound at src/Planted/HttpErrors.cs:1 sits in the 'Http.' family");
    }

    [Fact]
    public void The_scan_flags_a_computed_code_outside_the_SharedKernel()
    {
        const string ComputedCode = """
            public static Error Failed(string area) => Error.Failure(area + ".Failed", "It failed.");
            """;

        Vocabulary planted = Vocabulary.Read(
        [
            new SourceFile("src/Core/Planted.Domain/Errors.cs", ComputedCode),
            new SourceFile($"src/Core/{Architecture.SharedKernel}/Results/Errors.cs", ComputedCode),
        ]);

        planted.Undeclared.ShouldHaveSingleItem().ShouldStartWith("src/Core/Planted.Domain/Errors.cs:1");
    }

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*\.[A-Z][A-Za-z0-9]*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AreaDotProblem();

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*\.$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AreaPrefix();

    /// <summary><c>Error.Conflict(</c>, also qualified; the factory name is checked against <see cref="ErrorType"/> afterwards.</summary>
    [GeneratedRegex(@"(?<![\w.])(?:\w+\s*\.\s*)*?Error\s*\.\s*(?<factory>[A-Z]\w*)\s*(?<open>\()", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ErrorFactoryCall();

    [GeneratedRegex(@"\.\s*WithErrorCode\s*(?<open>\()", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WithErrorCodeCall();

    /// <summary>A <c>ProblemDescriptor</c> construction: a status (a <c>StatusCodes</c> constant or a <c>status</c> variable), then the code.</summary>
    [GeneratedRegex(@"\bnew(?:\s+ProblemDescriptor)?\s*(?<open>\()\s*(?:StatusCodes\s*\.\s*Status(?<status>\d{3})\w*|status\b)\s*,", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProblemDescriptorRow();

    /// <summary>A literal followed by <c>+</c>: a family of codes sharing the literal prefix.</summary>
    [GeneratedRegex("""^"(?<value>(?:[^"\\]|\\.)*)"\s*\+""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FamilyPrefix();

    [GeneratedRegex(@"^[A-Za-z_]\w*\s*:(?!:)\s*", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ArgumentName();

    /// <summary>One declared code (or family prefix), its meaning and where it was declared.</summary>
    private sealed record Site(string Code, string Kind, string Location, bool IsFamily);

    private sealed class Vocabulary
    {
        private static readonly HashSet<string> Factories = [.. Enum.GetNames<ErrorType>()];

        public List<Site> Codes { get; } = [];

        public List<string> Undeclared { get; } = [];

        public static Vocabulary Read(IEnumerable<SourceFile> source)
        {
            List<SourceFile> files = [.. source];
            var constants = new SourceConstants(files);
            var vocabulary = new Vocabulary();

            foreach (SourceFile file in files)
            {
                bool derivesCodes = file.RelativePath.Contains($"/{Architecture.SharedKernel}/", StringComparison.Ordinal);

                void Take(Match call, int position, string kind)
                {
                    string location = $"{file.RelativePath}:{file.LineOf(call.Index)}";
                    List<string> arguments = SourceConstants.ArgumentsAt(file.Code, call.Groups["open"].Index);
                    string argument = arguments.Count > position ? ArgumentName().Replace(arguments[position], string.Empty, 1) : string.Empty;

                    if (FamilyPrefix().Match(argument) is { Success: true } family)
                    {
                        vocabulary.Codes.Add(new Site(family.Groups["value"].Value, kind, location, IsFamily: true));
                    }
                    else if (constants.Resolve(file, argument) is { } code)
                    {
                        vocabulary.Codes.Add(new Site(code, kind, location, IsFamily: false));
                    }
                    else if (!derivesCodes)
                    {
                        vocabulary.Undeclared.Add($"{location}: {argument}");
                    }
                }

                foreach (Match call in ErrorFactoryCall().Matches(file.Code).Where(static m => Factories.Contains(m.Groups["factory"].Value)))
                {
                    Take(call, position: 0, kind: call.Groups["factory"].Value);
                }

                foreach (Match call in WithErrorCodeCall().Matches(file.Code))
                {
                    Take(call, position: 0, kind: nameof(ErrorType.Validation));
                }

                if (!file.Code.Contains("ProblemDescriptor", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match row in ProblemDescriptorRow().Matches(file.Code))
                {
                    Take(row, position: 1, kind: row.Groups["status"].Success ? $"HTTP {row.Groups["status"].Value}" : "HTTP status chosen at run time");
                }
            }

            return vocabulary;
        }

        public IEnumerable<string> MalformedCodes()
            => Codes
                .Where(static site => !(site.IsFamily ? AreaPrefix() : AreaDotProblem()).IsMatch(site.Code))
                .Select(static site => $"{site.Location}: '{site.Code}'");

        public IEnumerable<string> Collisions()
        {
            List<Site> exact = [.. Codes.Where(static site => !site.IsFamily)];

            IEnumerable<string> twoMeanings = exact
                .GroupBy(static site => site.Code, StringComparer.Ordinal)
                .Where(static declarations => declarations.Select(static site => site.Kind).Distinct(StringComparer.Ordinal).Count() > 1)
                .Select(static declarations => $"{declarations.Key}: {string.Join(", ", declarations.Select(static site => $"{site.Kind} at {site.Location}"))}");

            IEnumerable<string> insideAFamily = Codes
                .Where(static site => site.IsFamily)
                .SelectMany(family => exact
                    .Where(site => site.Code.StartsWith(family.Code, StringComparison.Ordinal)
                                   && !string.Equals(site.Kind, family.Kind, StringComparison.Ordinal))
                    .Select(site => $"{site.Code}: {site.Kind} at {site.Location} sits in the '{family.Code}' family ({family.Kind} at {family.Location})"));

            return twoMeanings.Concat(insideAFamily);
        }
    }
}
