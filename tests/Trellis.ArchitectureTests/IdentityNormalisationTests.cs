using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// Identity finds, creates and changes an account by the normalised form of its e-mail and user name, and the
/// normaliser throws an <see cref="ArgumentException"/> for text .NET calls invalid Unicode (U+FFFE, which the JSON
/// reader lets through): a call that hands it text a caller typed, unguarded, is a 500 that caller can ask for at
/// will. So every such call under <c>src/</c> and <c>tools/</c> sits in a file whose row in
/// <see cref="NormalisingCalls"/> counts it and says why each is safe: behind <c>IdentityService.CanBeLookedUp</c>,
/// which asks the normaliser first and answers a refusal as a <c>Result</c>, or on text no caller typed
/// (configuration, a constant, a name read back from the store). A new call fails the fact until its row says why,
/// in a listed file too, because the row's count no longer matches (<c>.claude/rules/persistence.md</c>, Identity).
/// <para>
/// The scan names the members only Identity spells that way, wherever they are written, called or as a method
/// group: <c>FindByEmailAsync</c>, <c>FindByNameAsync</c> (a role lookup too: <c>RoleManager</c> normalises a role
/// name with the same normaliser), <c>SetEmailAsync</c>, <c>SetUserNameAsync</c>, <c>ChangeEmailAsync</c>, and the
/// normaliser's own <c>NormalizeEmail</c> and <c>NormalizeName</c>. <c>CreateAsync</c> and <c>UpdateAsync</c>
/// normalise the account's e-mail and user name too (the user validator looks both up, then the normalised columns
/// are written), but every client and repository may have members of those names (the invoice API client's
/// <c>CreateAsync</c>), so the scan counts them only in a file that names <c>UserManager&lt;</c>, where they are
/// Identity's. What it does not see: a create or an update called on a user manager the file never names by that
/// type (a subclass injected under its own name), and a user store of your own, which is handed the value already
/// normalised.
/// </para>
/// </summary>
public sealed partial class IdentityNormalisationTests
{
    /// <summary>
    /// The files that hold calls that normalise an e-mail or a user name, keyed by repository-relative path: how
    /// many the scan finds there, and why each is safe. Read every call in the file before you change its count.
    /// </summary>
    private static readonly Dictionary<string, Row> NormalisingCalls = new(StringComparer.Ordinal)
    {
        ["src/Infrastructure/Trellis.Persistence/Identity/IdentityService.cs"] = new(
            5,
            "RegisterAsync's FindByEmailAsync, and the CreateAsync in CreateMemberAsync that follows it, run only after "
            + "CanBeLookedUp took the e-mail (the account's user name is the same e-mail, and the default normaliser "
            + "normalises a name and an e-mail alike); ValidateCredentialsAsync's FindByEmailAsync runs only when "
            + "CanBeLookedUp took it; CanBeLookedUp's own NormalizeEmail catches the refusal; BuildAuthenticatedUserAsync's "
            + "FindByNameAsync is a role lookup by a name GetRolesAsync read back from the store"),
        ["src/Infrastructure/Trellis.Persistence/Identity/IdentitySeed.cs"] = new(
            4,
            "the role FindByNameAsync and CreateAsync take the keys of Roles.Permissions, constants; the administrator's "
            + "FindByEmailAsync and CreateAsync take the e-mail its one caller, the migration worker, reads from "
            + "configuration (Seed:AdminEmail): no caller typed any of them"),
        ["tools/Trellis.MigrationService/MigrationWorker.cs"] = new(
            1,
            "the default-password guard's FindByEmailAsync takes Seed:AdminEmail from configuration: no caller typed it"),
    };

    [Fact]
    public void Every_Identity_call_that_normalises_an_e_mail_or_a_user_name_is_counted_with_the_reason_it_is_safe()
    {
        List<SourceFile> scanned = [.. RepositorySource.CSharpFiles("src", "tools")];
        Dictionary<string, string> reasons = NormalisingCalls.ToDictionary(static row => row.Key, static row => row.Value.Why, StringComparer.Ordinal);

        scanned.ShouldNotBeEmpty("the normalisation scan found no file under src/ or tools/; is the repository root right?");
        SourceScan.StaleEntries(scanned, reasons, NormalisingCallsIn).ShouldBeEmpty();
        SourceScan.Violations(scanned, reasons, NormalisingCallsIn).ShouldBeEmpty(
            "Identity normalises the e-mail or user name this call takes, and the normaliser throws ArgumentException for "
            + "text .NET calls invalid Unicode: text a caller typed asks IdentityService.CanBeLookedUp first and answers a "
            + "refusal as a Result (.claude/rules/persistence.md, Identity). Then list the file in NormalisingCalls with "
            + "how many such calls it holds and why each is safe");
        CountsTheRowsDoNotMatch(scanned, NormalisingCalls).ShouldBeEmpty(
            "a listed file holds a call its row does not count, or no longer holds one it does: read each call the "
            + "file holds, then give its row in NormalisingCalls the count and the reason each is safe");
    }

    /// <summary>
    /// Positive control for the scan, which expects every hit to be counted by its file's row: each member that
    /// normalises an e-mail or a user name is found, called or as a method group, and so are a create and an
    /// update in a file that names the user manager; a lookup by id, a password check, a create or an update in
    /// a file that does not, a cache's <c>GetOrCreateAsync</c> and EF Core's <c>ExecuteUpdateAsync</c> are not.
    /// The scan reads <c>src/</c> and <c>tools/</c>, not this file, but each row is still assembled from two
    /// literals, as the sleep scan's are, so the file stays clean if the scan ever reads <c>tests/</c>.
    /// </summary>
    [Theory]
    [InlineData("await userManager", ".FindByEmailAsync(email);", true)]
    [InlineData("await userManager", ".FindByNameAsync(userName);", true)]
    [InlineData("await roleManager", ".FindByNameAsync(roleName);", true)]
    [InlineData("await userManager", ".SetEmailAsync(user, email);", true)]
    [InlineData("await userManager", ".SetUserNameAsync(user, userName);", true)]
    [InlineData("await userManager", ".ChangeEmailAsync(user, newEmail, token);", true)]
    [InlineData("_ = userManager", ".NormalizeEmail(email);", true)]
    [InlineData("_ = normalizer", ".NormalizeName(name);", true)]
    [InlineData("var keys = emails.Select(userManager", ".NormalizeEmail);", true)]
    [InlineData("UserManager<ApplicationUser> users; await users", ".CreateAsync(user, password);", true)]
    [InlineData("UserManager<ApplicationUser> users; await users", ".UpdateAsync(user);", true)]
    [InlineData("await userManager", ".FindByIdAsync(userId.ToString());", false)]
    [InlineData("await userManager", ".CheckPasswordAsync(user, password);", false)]
    [InlineData("await invoiceApi", ".CreateAsync(request);", false)]
    [InlineData("await repository", ".UpdateAsync(order);", false)]
    [InlineData("UserManager<ApplicationUser> users; await cache", ".GetOrCreateAsync(key, factory);", false)]
    [InlineData("UserManager<ApplicationUser> users; await context.Tokens", ".ExecuteUpdateAsync(setters => setters.SetProperty(t => t.Used, true));", false)]
    public void The_normalisation_scan_flags_each_Identity_member_that_normalises_and_not_one_that_does_not(string receiver, string call, bool flagged)
        => NormalisingCallsIn(new SourceFile("Planted.cs", receiver + call)).Any().ShouldBe(flagged);

    /// <summary>
    /// Positive control for the count: a listed file that holds a call its row does not count is reported with
    /// every call it holds, so the row can be written from the failure; one whose row counts them all is not.
    /// </summary>
    [Fact]
    public void The_count_check_flags_a_listed_file_that_holds_a_call_its_row_does_not_count()
    {
        SourceFile planted = new("src/Planted.cs", "await userManager" + ".FindByEmailAsync(seeded);\nawait userManager" + ".FindByEmailAsync(typed);");

        CountsTheRowsDoNotMatch([planted], new Dictionary<string, Row>(StringComparer.Ordinal) { [planted.RelativePath] = new(1, "the seed's e-mail") })
            .ShouldHaveSingleItem().ShouldContain(planted.RelativePath + ":2:");
        CountsTheRowsDoNotMatch([planted], new Dictionary<string, Row>(StringComparer.Ordinal) { [planted.RelativePath] = new(2, "both from configuration") })
            .ShouldBeEmpty();
    }

    /// <summary>A file's row: how many calls that normalise an e-mail or a user name it holds, and why each is safe.</summary>
    private sealed record Row(int Calls, string Why);

    /// <summary>
    /// Each listed file whose calls the scan counts differently from its row, as the path, both counts and every
    /// call the file holds (<c>path:line: text</c>). A row naming no scanned file is <see cref="SourceScan.StaleEntries"/>'s.
    /// </summary>
    private static List<string> CountsTheRowsDoNotMatch(IEnumerable<SourceFile> files, IReadOnlyDictionary<string, Row> rows)
        => [.. files
            .Where(file => rows.ContainsKey(file.RelativePath))
            .Select(file => (File: file, Found: NormalisingCallsIn(file).ToList(), Counted: rows[file.RelativePath].Calls))
            .Where(static entry => entry.Found.Count != entry.Counted)
            .Select(static entry =>
                $"{entry.File.RelativePath}: the scan finds {entry.Found.Count} calls, its row counts {entry.Counted}: "
                + string.Join(" | ", entry.Found.Select(entry.File.Describe)))];

    /// <summary>Where <paramref name="file"/> calls an Identity member that normalises an e-mail or a user name, in order.</summary>
    private static IEnumerable<int> NormalisingCallsIn(SourceFile file)
    {
        IEnumerable<Match> hits = NormalisingMember().Matches(file.Code);
        if (NamesTheUserManager().IsMatch(file.Code))
        {
            hits = hits.Concat(CreateOrUpdate().Matches(file.Code));
        }

        return hits.Select(static match => match.Index).Order();
    }

    /// <summary>
    /// A member that normalises an e-mail or a user name under a name only Identity uses, called or as a method
    /// group (<c>UpdateNormalizedEmailAsync</c> is another word and normalises what the account already holds).
    /// </summary>
    [GeneratedRegex(
        @"\b(?:FindByEmailAsync|FindByNameAsync|SetEmailAsync|SetUserNameAsync|ChangeEmailAsync|NormalizeEmail|NormalizeName)\b",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex NormalisingMember();

    /// <summary>A create or an update by a name clients and repositories use too, counted only where the file names the user manager.</summary>
    [GeneratedRegex(@"\b(?:CreateAsync|UpdateAsync)\s*\(", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CreateOrUpdate();

    /// <summary>The user manager's type, generic, as a field, a parameter, a resolved service or a base class names it.</summary>
    [GeneratedRegex(@"\bUserManager\s*<", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamesTheUserManager();
}
