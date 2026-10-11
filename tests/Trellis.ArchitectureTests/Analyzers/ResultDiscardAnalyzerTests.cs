using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using Microsoft.AspNetCore.Identity;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Shouldly;
using Trellis.Analyzers;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.ArchitectureTests.Analyzers;

/// <summary>
/// RSLT001 (ADR-0030): a statement that drops a <see cref="Result"/>, a <see cref="Result{TValue}"/> or an
/// <see cref="IdentityResult"/> fails the Release build. Each case compiles a snippet against the real
/// SharedKernel and the test host's trusted platform assemblies and runs the analyzer the build loads;
/// the last facts prove the build loads it everywhere and that the repository's own configuration turns
/// a planted discard into an error.
/// </summary>
public sealed class ResultDiscardAnalyzerTests
{
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Identity;
        using Trellis.SharedKernel.Results;
        """;

    /// <summary>What the snippets call: every shape of result-returning member the analyzer must see.</summary>
    private const string Fixture = """
        public sealed class Account
        {
            public Result? Last { get; set; }
            public Result Close() => Result.Success();
            public Result<int> Withdraw(int amount) => Result.Success(amount);
            public Task<Result> CloseAsync() => Task.FromResult(Result.Success());
            public ValueTask<Result<int>> WithdrawAsync(int amount) => new(Result.Success(amount));
            public Task SaveAsync() => Task.CompletedTask;
            public Task<IdentityResult> CreateUserAsync() => Task.FromResult(IdentityResult.Success);
            public Result[] CloseAll() => [Close()];
            public IEnumerable<Result> Closings() { yield return Close(); }
            public async IAsyncEnumerable<Result> ClosingsAsync() { await Task.Yield(); yield return Close(); }
            public (Result Outcome, int Lines) CloseAndCount() => (Close(), 1);
        }

        public sealed class Marked : Result
        {
            public Marked() : base(true, null) { }
            public Marked(object marker) : this() => GC.KeepAlive(marker);
        }

        public sealed class LineBook : IEnumerable<string>
        {
            private readonly List<string> _lines = [];
            public Result Add(string line) { _lines.Add(line); return Result.Success(); }
            public IEnumerator<string> GetEnumerator() => _lines.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public static class AccountErrors
        {
            public static Error NotFound(int id) => Error.NotFound("Account.NotFound", $"No account {id}.");
        }
        """;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static readonly CSharpCompilationOptions DefaultOptions =
        new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(LoadReferences);

    /// <summary>The folders whose projects must all load the analyzer; eng/ has its own empty props on purpose.</summary>
    private static readonly string[] ProjectFolders = ["src", "tests", "tools"];

    [Theory]
    [InlineData("a Result call", "account.Close();")]
    [InlineData("a Result<T> call", "account.Withdraw(5);")]
    [InlineData("a static factory call", "Result.Success();")]
    [InlineData("an awaited Task<Result>", "await account.CloseAsync();")]
    [InlineData("an awaited ValueTask<Result<T>>", "await account.WithdrawAsync(5);")]
    [InlineData("an awaited task behind ConfigureAwait", "await account.CloseAsync().ConfigureAwait(false);")]
    [InlineData("a conditional access", "maybe?.Close();")]
    [InlineData("an expression-bodied void lambda", "Action close = () => account.Close(); close();")]
    [InlineData("an expression-bodied async lambda returning Task", "Func<Task> close = async () => await account.CloseAsync(); await close();")]
    [InlineData("an expression-bodied local function", "void CloseIt() => account.Close(); CloseIt();")]
    [InlineData("an awaited IdentityResult", "await account.CreateUserAsync();")]
    [InlineData("an object creation", "new IdentityResult();")]
    [InlineData("a type parameter constrained to Result", "static void Drop<T>(Func<T> make) where T : Result { make(); } Drop(() => account.Close());")]
    [InlineData("a Task<Result> returned as a plain Task", "Task Plain() => account.CloseAsync(); await Plain();")]
    [InlineData("a Task<Result> lambda typed Func<Task>", "Func<Task> work = () => account.CloseAsync(); await work();")]
    [InlineData("a Task<Result> method group typed Func<Task>", "Func<Task> work = account.CloseAsync; await work();")]
    [InlineData("a Task<Result> stored as a plain Task", "Task plain = account.CloseAsync(); await plain;")]
    [InlineData("a Task<Result> cast to Task", "await (Task)account.CloseAsync();")]
    [InlineData("a Task<IdentityResult> stored as a plain Task", "Task created = account.CreateUserAsync(); await created;")]
    [InlineData("a Task<Result> passed as a plain Task", "await Task.WhenAny(account.CloseAsync(), Task.Delay(1));")]
    [InlineData("a Task<Result> through a conditional access stored as a plain Task", "Task? plain = maybe?.CloseAsync(); await (plain ?? Task.CompletedTask);")]
    [InlineData("Task<Result>s seen as plain Tasks", "IEnumerable<Task> all = new[] { account }.Select(a => a.CloseAsync()); await Task.WhenAll(all);")]
    [InlineData("a Task<Result> waited on as a plain Task", "Task.WaitAll(account.CloseAsync());")]
    [InlineData("an awaited Task.WhenAll of Task<Result>s", "await Task.WhenAll(account.CloseAsync(), account.CloseAsync());")]
    [InlineData("an awaited Task.WhenAll over a projection", "await Task.WhenAll(new[] { account }.Select(a => a.CloseAsync()));")]
    [InlineData("an awaited Task.WhenAll of IdentityResults", "await Task.WhenAll(account.CreateUserAsync(), account.CreateUserAsync());")]
    [InlineData("an awaited Task.WhenAny of Task<Result>s", "await Task.WhenAny(account.CloseAsync(), account.CloseAsync());")]
    [InlineData("a Result[]", "account.CloseAll();")]
    [InlineData("an IEnumerable<Result> iterator that never runs", "account.Closings();")]
    [InlineData("an IAsyncEnumerable<Result>", "account.ClosingsAsync();")]
    [InlineData("a List.ConvertAll to results", "new List<Account> { account }.ConvertAll(a => a.Close());")]
    [InlineData("an Array.ConvertAll to results", "Array.ConvertAll(new[] { account }, a => a.Close());")]
    [InlineData("a projection to results", "new[] { account }.Select(a => a.Close());")]
    [InlineData("a tuple holding a Result", "account.CloseAndCount();")]
    [InlineData("an Error factory call, its return forgotten", "if (maybe is null) { AccountErrors.NotFound(1); }")]
    [InlineData("an Error static factory", "Error.Conflict(\"Account.Locked\", \"The account is locked.\");")]
    [InlineData("a FieldError creation", "new FieldError(\"amount\", \"Money.Negative\", \"The amount is negative.\");")]
    [InlineData("a ValidationError creation", "new ValidationError([]);")]
    [InlineData("a Task<Result> waited on", "account.CloseAsync().Wait();")]
    [InlineData("a Task<Result> waited on with a timeout", "account.CloseAsync().Wait(TimeSpan.FromSeconds(1));")]
    [InlineData("a collection initializer whose Add returns a Result", "LineBook book = new() { \"first line\" }; GC.KeepAlive(book);")]
    [InlineData("a dynamic creation of a Result type", "dynamic marker = 1; new Marked(marker);")]
    [InlineData("a new() of a type parameter constrained to Result", "static void Make<T>() where T : Result, new() { new T(); } Make<Marked>();")]
    public async Task A_dropped_result_is_reported(string shape, string statements)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(InRunMethod(statements));

        diagnostics.ShouldHaveSingleItem($"{shape}: `{statements}`").Id.ShouldBe(ResultDiscardAnalyzer.DiagnosticId);
    }

    [Theory]
    [InlineData("an explicit discard", "_ = account.Close();")]
    [InlineData("a local assignment", "Result closed = account.Close(); GC.KeepAlive(closed);")]
    [InlineData("a property assignment", "account.Last = account.Close();")]
    [InlineData("a null-conditional assignment", "maybe?.Last = account.Close();")]
    [InlineData("a coalescing assignment", "account.Last ??= account.Close();")]
    [InlineData("a return", "Result Closed() { return account.Close(); } _ = Closed();")]
    [InlineData("an argument", "GC.KeepAlive(account.Close());")]
    [InlineData("a Func<Result> lambda", "Func<Result> close = () => account.Close(); _ = close();")]
    [InlineData("an async Func<Task<Result>> lambda", "Func<Task<Result>> close = async () => await account.CloseAsync(); _ = await close();")]
    [InlineData("an awaited plain Task", "await account.SaveAsync();")]
    [InlineData("a List.Add call", "var results = new List<Result>(); results.Add(account.Close());")]
    [InlineData("a held Task<Result> also seen as a plain Task", "Task<Result> closing = account.CloseAsync(); Task plain = closing; await plain; _ = await closing;")]
    [InlineData("a Task<Result> returned as itself", "Task<Result> Closing() => account.CloseAsync(); _ = await Closing();")]
    [InlineData("an Error returned as a Result", "Result Refuse() { return AccountErrors.NotFound(1); } _ = Refuse();")]
    [InlineData("a held Task<Result> waited on and read", "Task<Result> closing = account.CloseAsync(); closing.Wait(); _ = closing.Result;")]
    [InlineData("a plain Task waited on", "account.SaveAsync().Wait();")]
    [InlineData("a collection initializer of results", "List<Result> results = new() { account.Close() }; GC.KeepAlive(results);")]
    [InlineData("a kept Result[]", "Result[] all = account.CloseAll(); GC.KeepAlive(all);")]
    [InlineData("a deconstructed tuple", "(Result outcome, int lines) = account.CloseAndCount(); GC.KeepAlive(outcome); GC.KeepAlive(lines);")]
    [InlineData("a dropped sequence of plain values", "new[] { 1 }.Select(static i => i + 1).ToList();")]
    [InlineData("an awaited Task.WhenAll of plain Tasks", "await Task.WhenAll(account.SaveAsync(), account.SaveAsync());")]
    public async Task A_result_that_is_kept_or_dropped_on_purpose_is_not_reported(string shape, string statements)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(InRunMethod(statements));

        diagnostics.ShouldBeEmpty($"{shape}: `{statements}`");
    }

    [Fact]
    public async Task The_diagnostic_names_the_type_and_points_at_the_dropped_expression()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(InRunMethod("account.Withdraw(5);"));

        Diagnostic diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Warning);
        diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldStartWith("This Result<int> is discarded");
        (await diagnostic.Location.SourceTree.ShouldNotBeNull().GetTextAsync(TestContext.Current.CancellationToken))
            .ToString(diagnostic.Location.SourceSpan).ShouldBe("account.Withdraw(5)");
    }

    /// <summary>Two messages (a dropped result, a task of one turned into a plain Task), one rule: id, severity, title and ADR.</summary>
    [Fact]
    public void The_rule_is_a_warning_by_default_and_its_help_link_is_the_ADR()
    {
        ImmutableArray<DiagnosticDescriptor> descriptors = new ResultDiscardAnalyzer().SupportedDiagnostics;

        descriptors.Length.ShouldBe(2);
        string helpLink = descriptors[0].HelpLinkUri;
        string title = descriptors[0].Title.ToString(System.Globalization.CultureInfo.InvariantCulture);
        descriptors.ShouldAllBe(rule => rule.Id == "RSLT001"
            && rule.DefaultSeverity == DiagnosticSeverity.Warning
            && rule.IsEnabledByDefault
            && rule.HelpLinkUri == helpLink
            && rule.Title.ToString(System.Globalization.CultureInfo.InvariantCulture) == title);
        File.Exists(Path.Combine(RepositorySource.Root.FullName, helpLink))
            .ShouldBeTrue($"the help link '{helpLink}' must name the ADR file");
    }

    [Fact]
    public async Task An_expression_bodied_void_member_that_drops_a_result_is_reported()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync($$"""
            {{Usings}}

            {{Fixture}}

            public sealed class Teller
            {
                public void CloseFor(Account account) => account.Close();
            }
            """);

        diagnostics.ShouldHaveSingleItem().Id.ShouldBe(ResultDiscardAnalyzer.DiagnosticId);
    }

    /// <summary>
    /// The shape of an outbox handler on top of an ADR-0020 port: <c>Handle</c> returns a plain
    /// <see cref="Task"/>, the port a <c>Task&lt;Result&gt;</c>, and the conversion between them drops the
    /// failure, so the outbox checkpoints a failed push as done and never retries it.
    /// </summary>
    [Fact]
    public async Task A_task_of_a_result_returned_as_a_plain_task_is_reported_with_both_types()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync($$"""
            {{Usings}}

            {{Fixture}}

            public sealed class PushOnClose
            {
                public Task Handle(Account account) => account.CloseAsync();
            }
            """);

        Diagnostic diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe(ResultDiscardAnalyzer.DiagnosticId);
        diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            .ShouldStartWith("This Task<Result> is converted to Task, which drops its result");
        (await diagnostic.Location.SourceTree.ShouldNotBeNull().GetTextAsync(TestContext.Current.CancellationToken))
            .ToString(diagnostic.Location.SourceSpan).ShouldBe("account.CloseAsync()");
    }

    /// <summary>
    /// A delegate handed to an exception assertion is expected to throw, so the call in its body has
    /// no result to lose: Shouldly's <c>Should.ThrowAsync</c> and xUnit's <c>Assert.ThrowsAsync</c> take
    /// the plain <see cref="Task"/> the conversion produces. A stand-in with their names keeps both
    /// libraries out of the snippet.
    /// </summary>
    [Theory]
    [InlineData("await Should.ThrowAsync<InvalidOperationException>(() => account.CloseAsync());")]
    [InlineData("await Should.ThrowAsync<InvalidOperationException>(async () => await account.CloseAsync());")]
    [InlineData("await Should.ThrowAsync<InvalidOperationException>(account.CloseAsync());")]
    [InlineData("await Should.ThrowAsync<InvalidOperationException>(account.CloseAsync);")]
    [InlineData("await Assert.ThrowsAsync<InvalidOperationException>(() => account.CloseAsync());")]
    public async Task A_call_an_exception_assertion_expects_to_throw_is_not_reported(string statements)
    {
        const string assertionStandIns = """
            public static class Should
            {
                public static Task ThrowAsync<TException>(Func<Task> actual) where TException : Exception => actual();
                public static Task ThrowAsync<TException>(Task actual) where TException : Exception => actual;
            }

            public static class Assert
            {
                public static Task ThrowsAsync<TException>(Func<Task> testCode) where TException : Exception => testCode();
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(InRunMethod(statements) + assertionStandIns);

        diagnostics.ShouldBeEmpty(statements);
    }

    [Fact]
    public async Task Generated_code_is_not_reported()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync("// <auto-generated/>\n" + InRunMethod("account.Close();"));

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_NSubstitute_call_check_is_not_reported_but_a_real_call_on_a_substitute_is()
    {
        // The substitute library's check returns a placeholder for the call it asserts; a stand-in with
        // the same namespace and method names keeps NSubstitute itself out of this project.
        const string substituteStandIn = """
            namespace NSubstitute
            {
                public static class SubstituteExtensions
                {
                    public static T Received<T>(this T substitute, int requiredNumberOfCalls = 1) => substitute;
                    public static T DidNotReceive<T>(this T substitute) => substitute;
                }
            }
            """;

        ImmutableArray<Diagnostic> checks = await AnalyzeAsync(
            InRunMethod("await account.Received(1).CloseAsync(); account.DidNotReceive().Close();", "using NSubstitute;") + substituteStandIn);
        ImmutableArray<Diagnostic> realCall = await AnalyzeAsync(
            InRunMethod("await account.Received(1).CloseAsync(); await account.CloseAsync();", "using NSubstitute;") + substituteStandIn);

        checks.ShouldBeEmpty();
        realCall.ShouldHaveSingleItem().Id.ShouldBe(ResultDiscardAnalyzer.DiagnosticId);
    }

    /// <summary>
    /// An assertion that hands back the value it checked, for chaining (<c>result.Error.ShouldBeOfType&lt;ValidationError&gt;()</c>,
    /// xUnit's <c>Assert.IsType&lt;T&gt;</c>, a <c>[ShouldlyMethods]</c> helper of our own): checking the value is
    /// what the test does with it. Stand-ins with the libraries' namespaces keep both out of the snippet.
    /// </summary>
    [Theory]
    [InlineData("account.Close().Error.ShouldBeOfType<ValidationError>();")]
    [InlineData("account.Close().ShouldNotBeNull();")]
    [InlineData("Assert.IsType<Result>(account.Close());")]
    [InlineData("account.Close().ShouldBeChecked();")]
    public async Task An_assertion_that_hands_back_the_checked_value_is_not_reported(string statements)
    {
        const string assertionStandIns = """
            namespace Shouldly
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class ShouldlyMethodsAttribute : Attribute;

                public static class ShouldBeTestExtensions
                {
                    public static T ShouldBeOfType<T>(this object? actual) => (T)actual!;
                    public static T ShouldNotBeNull<T>(this T? actual) where T : class => actual!;
                }
            }

            namespace Xunit
            {
                public static class Assert
                {
                    public static T IsType<T>(object? @object) => (T)@object!;
                }
            }

            [Shouldly.ShouldlyMethods]
            public static class OwnAssertions
            {
                public static Result ShouldBeChecked(this Result result) => result;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            InRunMethod(statements, "using Shouldly;\nusing Xunit;") + assertionStandIns);

        diagnostics.ShouldBeEmpty(statements);
    }

    /// <summary>
    /// An unawaited task of a result is reported exactly once. The compiler's CS4014 covers a call
    /// written as a statement inside an async method, lambda or local function; everywhere it is silent
    /// (a sync method, a non-async lambda, a conditional access) RSLT001 reports it instead.
    /// </summary>
    [Theory]
    [InlineData(true, "account.CloseAsync();", "CS4014")]
    [InlineData(true, "account.WithdrawAsync(5);", "CS4014")]
    [InlineData(true, "Func<Task> close = async () => { account.CloseAsync(); await Task.Yield(); }; await close();", "CS4014")]
    [InlineData(true, "async Task CloseLater() { account.CloseAsync(); await Task.Yield(); } await CloseLater();", "CS4014")]
    [InlineData(true, "maybe?.CloseAsync();", "RSLT001")]
    [InlineData(true, "Action close = () => account.CloseAsync(); close();", "RSLT001")]
    [InlineData(false, "account.CloseAsync();", "RSLT001")]
    [InlineData(false, "account.CreateUserAsync();", "RSLT001")]
    [InlineData(false, "Task.Run(() => account.CloseAsync());", "RSLT001")]
    public async Task An_unawaited_task_of_a_result_is_reported_once_by_CS4014_or_RSLT001(bool inAsyncMethod, string statements, string reportedBy)
    {
        string source = InRunMethod(statements, isAsync: inAsyncMethod);

        ImmutableArray<Diagnostic> analyzer = await AnalyzeAsync(source);
        bool compiler = Compile(source).GetDiagnostics(TestContext.Current.CancellationToken).Any(static d => d.Id == "CS4014");

        (analyzer.Length, compiler).ShouldBe(reportedBy == "CS4014" ? (0, true) : (1, false), $"`{statements}`");
    }

    /// <summary>
    /// The structural half of "every project": the one reference in the root props, with the metadata
    /// that loads the analyzer without referencing or copying it, conditioned on the analyzer's own name;
    /// and every project under src/, tests/ and tools/ inherits that props file, directly or through a
    /// nested one that imports it.
    /// </summary>
    [Fact]
    public void Directory_Build_props_loads_the_analyzer_into_every_project_but_its_own()
    {
        string root = RepositorySource.Root.FullName;
        string analyzerName = typeof(ResultDiscardAnalyzer).Assembly.GetName().Name.ShouldNotBeNull();
        XDocument props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));

        XElement reference = props.Descendants("ProjectReference").ShouldHaveSingleItem();
        reference.Attribute("Include")?.Value.ShouldBe("$(ResultAnalyzerProject)");
        reference.Attribute("OutputItemType")?.Value.ShouldBe("Analyzer");
        reference.Attribute("ReferenceOutputAssembly")?.Value.ShouldBe("false");
        reference.Attribute("PrivateAssets")?.Value.ShouldBe("all");
        reference.Parent.ShouldNotBeNull().Attribute("Condition")?.Value
            .ShouldBe($"'$(MSBuildProjectName)' != '{analyzerName}'");

        string analyzerProject = props.Descendants("ResultAnalyzerProject").ShouldHaveSingleItem().Value
            .Replace("$(MSBuildThisFileDirectory)", string.Empty, StringComparison.Ordinal);
        Path.GetFileNameWithoutExtension(analyzerProject).ShouldBe(analyzerName);
        File.Exists(Path.Combine(root, analyzerProject)).ShouldBeTrue($"{analyzerProject} must exist");

        List<string> projects = [.. ProjectFolders
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(static folder => Directory.EnumerateFiles(folder, "*.csproj", SearchOption.AllDirectories))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
        projects.Count.ShouldBeGreaterThanOrEqualTo(10);

        List<string> cutOff = [.. projects.Where(project => !InheritsRootProps(root, project))];
        cutOff.ShouldBeEmpty("these projects do not import the root Directory.Build.props, so RSLT001 never runs in them");
        List<string> opting = [.. projects.Where(static project =>
            File.ReadAllText(project).Contains("ImportDirectoryBuildProps", StringComparison.Ordinal)
            || File.ReadAllText(project).Contains("Remove=\"$(ResultAnalyzerProject)\"", StringComparison.Ordinal))];
        opting.ShouldBeEmpty("these projects opt out of the root props or remove the analyzer");
    }

    /// <summary>
    /// The positive control for the build: real Domain, Application and SharedKernel calls dropped in a
    /// planted file, compiled under the repository's own .editorconfig (severities and generated-code
    /// sections, as the compiler reads them) with warnings as errors, as a Release build compiles. Each
    /// drop is an error; the same file under Migrations/ is generated code and is left alone.
    /// </summary>
    [Theory]
    [InlineData("src/Core/Trellis.Application/Planted.cs", 3)]
    [InlineData("src/Infrastructure/Trellis.Persistence/Planted.cs", 3)]
    [InlineData("tests/Trellis.IntegrationTests/Planted.cs", 3)]
    [InlineData("tools/Trellis.MigrationService/Planted.cs", 3)]
    [InlineData("src/Infrastructure/Trellis.Persistence/Migrations/Planted.cs", 0)]
    public async Task The_repositorys_configuration_fails_a_Release_build_on_a_planted_discard(string plantedAt, int expectedErrors)
    {
        const string planted = """
            using System.Threading;
            using System.Threading.Tasks;
            using Trellis.Application.Abstractions;
            using Trellis.Domain.Common;
            using Trellis.SharedKernel.Results;

            public static class Planted
            {
                public static async Task Run(IIdentityService identity, CancellationToken cancellationToken)
                {
                    EmailAddress.Create("someone@example.com");
                    Result.Combine(Result.Success(), Result.Success());
                    await identity.RegisterAsync("someone@example.com", "Secret-123", cancellationToken);
                }
            }
            """;
        string root = RepositorySource.Root.FullName;
        string editorConfigPath = Path.Combine(root, ".editorconfig");
        AnalyzerConfigSet editorConfig = AnalyzerConfigSet.Create(ImmutableArray.Create(
            AnalyzerConfig.Parse(await File.ReadAllTextAsync(editorConfigPath, TestContext.Current.CancellationToken), editorConfigPath)));
        CSharpCompilationOptions release = DefaultOptions
            .WithGeneralDiagnosticOption(ReportDiagnostic.Error)
            .WithSyntaxTreeOptionsProvider(new EditorConfigTreeOptions(editorConfig));

        string plantedPath = Path.Combine(root, plantedAt.Replace('/', Path.DirectorySeparatorChar));

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            planted, plantedPath, release, new AnalyzerOptions([], new EditorConfigAnalyzerOptions(editorConfig)));

        diagnostics.Count(static d => d.Id == ResultDiscardAnalyzer.DiagnosticId && d.Severity == DiagnosticSeverity.Error)
            .ShouldBe(expectedErrors, $"RSLT001 errors in {plantedAt}");
        diagnostics.ShouldAllBe(static d => d.Severity == DiagnosticSeverity.Error);
    }

    private static string InRunMethod(string statements, string extraUsings = "", bool isAsync = true) => $$"""
        {{Usings}}
        {{extraUsings}}

        {{Fixture}}

        public static class Snippet
        {
            public static {{(isAsync ? "async Task" : "void")}} Run(Account account, Account? maybe)
            {
                {{statements}}
            }
        }
        """;

    private static CSharpCompilation Compile(string source, string path = "Snippet.cs", CSharpCompilationOptions? options = null)
        => CSharpCompilation.Create(
            "Snippet",
            [CSharpSyntaxTree.ParseText(source, ParseOptions, path, cancellationToken: TestContext.Current.CancellationToken)],
            References.Value,
            options ?? DefaultOptions);

    /// <summary>Runs RSLT001 over a snippet that must compile: a snippet with a typo would prove nothing either way.</summary>
    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source,
        string path = "Snippet.cs",
        CSharpCompilationOptions? options = null,
        AnalyzerOptions? analyzerOptions = null)
    {
        CSharpCompilation compilation = Compile(source, path, options);
        compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static d => d.Severity == DiagnosticSeverity.Error && !d.IsWarningAsError)
            .ShouldBeEmpty("the snippet must compile");

        return await compilation
            .WithAnalyzers([new ResultDiscardAnalyzer()], analyzerOptions)
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The real SharedKernel and Identity assemblies, plus everything the test host itself runs on.</summary>
    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        string trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        return [.. trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(Result).Assembly.Location)
            .Append(typeof(IdentityResult).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path))];
    }

    /// <summary>A project directory's nearest Directory.Build.props is the root one, or one that imports the file above it.</summary>
    private static bool InheritsRootProps(string root, string project)
    {
        string rootProps = Path.Combine(root, "Directory.Build.props");
        for (DirectoryInfo? directory = new FileInfo(project).Directory; directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "Directory.Build.props");
            if (string.Equals(candidate, rootProps, StringComparison.Ordinal))
            {
                return true;
            }

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate).Contains("GetPathOfFileAbove(Directory.Build.props", StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>The compiler's view of .editorconfig for one file: the per-rule severities.</summary>
    private sealed class EditorConfigTreeOptions(AnalyzerConfigSet editorConfig) : SyntaxTreeOptionsProvider
    {
        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken)
            => editorConfig.GetOptionsForSourcePath(tree.FilePath).AnalyzerOptions.TryGetValue("generated_code", out string? generated)
               && string.Equals(generated, "true", StringComparison.OrdinalIgnoreCase)
                ? GeneratedKind.MarkedGenerated
                : GeneratedKind.Unknown;

        public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
            => editorConfig.GetOptionsForSourcePath(tree.FilePath).TreeOptions.TryGetValue(diagnosticId, out severity);

        public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
            => editorConfig.GlobalConfigOptions.TreeOptions.TryGetValue(diagnosticId, out severity);
    }

    /// <summary>
    /// The analyzers' view of .editorconfig for one file, where the analyzer driver reads
    /// <c>generated_code</c>: the switch that sends the Migrations/ section past every analyzer.
    /// </summary>
    private sealed class EditorConfigAnalyzerOptions(AnalyzerConfigSet editorConfig) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions => new Values(editorConfig.GlobalConfigOptions.AnalyzerOptions);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree)
            => new Values(editorConfig.GetOptionsForSourcePath(tree.FilePath).AnalyzerOptions);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile)
            => new Values(editorConfig.GetOptionsForSourcePath(textFile.Path).AnalyzerOptions);

        private sealed class Values(ImmutableDictionary<string, string> values) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => values.TryGetValue(key, out value);
        }
    }
}
