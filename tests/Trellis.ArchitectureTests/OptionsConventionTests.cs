using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// The options rule (.claude/rules/persistence.md, infrastructure.md): every configuration-bound
/// <c>*Options</c> type states its limits as DataAnnotations next to the property, and the host that
/// uses it checks them at boot — never a <c>Get&lt;T&gt;() ?? new T()</c> that turns a typo into
/// silently-defaulted behaviour (a zero sweep interval that hot-loops, an empty signing key).
/// </summary>
public sealed partial class OptionsConventionTests
{
    private const string SigningKey = "architecture-tests-signing-key-0123456789abcdef";

    private static readonly Assembly[] OptionsAssemblies =
    [
        Architecture.ApplicationAssembly,
        Architecture.InfrastructureAssembly,
        Architecture.PersistenceAssembly,
        Architecture.ApiAssembly,
        typeof(Api.Common.DependencyInjection).Assembly,
    ];

    /// <summary>The configuration-bound options types of the inner rings and the API host.</summary>
    public static TheoryData<Type> OptionsTypes() => [.. DiscoverOptionsTypes()];

    [Fact]
    public void The_scan_finds_the_options_types_it_is_meant_to_police()
        => DiscoverOptionsTypes().Select(static type => type.Name).ShouldContain("RetentionOptions", StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(OptionsTypes))]
    public void Every_options_type_declares_its_limits_as_validation_attributes(Type optionsType)
        => optionsType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(static property => property.GetCustomAttributes<ValidationAttribute>(inherit: true).Any())
            .ShouldBeTrue($"{optionsType.FullName} has no ValidationAttribute: put [Required]/[Range]/... next to the properties it binds");

    [Fact]
    public async Task The_api_host_registers_a_validator_for_every_options_type()
    {
        await using BuiltHost host = BuiltHost.Of(typeof(Program).Assembly, ValidArguments());

        DiscoverOptionsTypes()
            .Where(optionsType => !host.Services.GetServices(typeof(IValidateOptions<>).MakeGenericType(optionsType)).Any())
            .Select(static optionsType => optionsType.FullName)
            .ShouldBeEmpty("the API host binds these without validating them: add ValidateDataAnnotations() (and ValidateOnStart() in the host)");
    }

    [Fact]
    public async Task The_api_host_passes_its_boot_validation_with_valid_configuration()
    {
        await using BuiltHost host = BuiltHost.Of(typeof(Program).Assembly, ValidArguments());

        Should.NotThrow(() => host.Services.GetRequiredService<IStartupValidator>().Validate());
    }

    /// <summary>
    /// One out-of-range key on the API's command line stops the boot, and the message names it. The
    /// per-property limits are DataAnnotations; a cross-field rule (registered next to the binding) states
    /// its own message, passed as <paramref name="expected"/> when it is more than the key.
    /// </summary>
    [Theory]
    [InlineData("Retention", "SweepInterval", "00:00:00")]
    [InlineData("Retention", "SweepInterval", "-00:00:01")]
    [InlineData("Retention", "InitialDelay", "-00:00:01")]
    [InlineData("Retention", "ProcessedOutboxAge", "00:00:00")]
    [InlineData("Retention", "DeadLetteredOutboxAge", "-1.00:00:00")]
    [InlineData("Retention", "IdempotencyKeyAge", "00:00:00")]
    [InlineData("Retention", "DeadRefreshTokenAge", "00:00:01")]
    [InlineData("Outbox", "BatchSize", "0")]
    [InlineData("Outbox", "PollInterval", "00:00:00")]
    // In range on its own (1 s .. 1 day) but below the default BaseRetryDelay of 30 s: only the cross-field rule rejects it.
    [InlineData("Outbox", "MaxRetryDelay", "00:00:10", "Outbox:MaxRetryDelay must be >= Outbox:BaseRetryDelay")]
    public async Task An_out_of_range_setting_stops_the_api_at_boot_naming_the_key(string section, string key, string value, string? expected = null)
    {
        await using BuiltHost host = BuiltHost.Of(typeof(Program).Assembly, [.. ValidArguments(), $"--{section}:{key}={value}"]);

        OptionsValidationException exception = Should.Throw<OptionsValidationException>(
            () => host.Services.GetRequiredService<IStartupValidator>().Validate());

        exception.Message.ShouldContain(expected ?? $"{section}:{key}");
    }

    /// <summary>
    /// Hosts under <c>tools/</c> are not referenced by this project, so their options are checked in
    /// source: each <c>*Options</c> class declares a validation attribute, and its project binds it with
    /// <c>ValidateDataAnnotations()</c> and <c>ValidateOnStart()</c> in the same registration.
    /// </summary>
    [Fact]
    public void Every_options_type_in_a_tools_host_is_annotated_and_validated_at_boot()
    {
        var violations = new List<string>();
        SourceFile[] files = [.. RepositorySource.CSharpFiles("tools")];

        foreach (SourceFile file in files)
        {
            foreach (Match declaration in OptionsClassDeclaration().Matches(file.Code))
            {
                string name = declaration.Groups["name"].Value;

                if (!ValidationAttributeUsage().IsMatch(file.Code))
                {
                    violations.Add($"{file.RelativePath}: {name} declares no validation attribute");
                }

                int projectEnd = file.RelativePath.IndexOf('/', "tools/".Length);
                string project = projectEnd < 0 ? "tools" : file.RelativePath[..projectEnd];
                bool validatedAtBoot = files
                    .Where(other => other.RelativePath.StartsWith(project + "/", StringComparison.Ordinal))
                    .SelectMany(other => Registrations(other.Code, name))
                    .Any(static registration => registration.Contains(".ValidateDataAnnotations()", StringComparison.Ordinal)
                                                && registration.Contains(".ValidateOnStart()", StringComparison.Ordinal));

                if (!validatedAtBoot)
                {
                    violations.Add($"{file.RelativePath}: {name} is not registered with AddOptions<{name}>()...ValidateDataAnnotations().ValidateOnStart()");
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    /// <summary>Each <c>AddOptions&lt;name&gt;()</c> statement in <paramref name="code"/>, up to its semicolon.</summary>
    private static IEnumerable<string> Registrations(string code, string name)
    {
        string call = $"AddOptions<{name}>()";
        for (int start = code.IndexOf(call, StringComparison.Ordinal); start >= 0; start = code.IndexOf(call, start + call.Length, StringComparison.Ordinal))
        {
            int end = code.IndexOf(';', start);
            yield return end < 0 ? code[start..] : code[start..end];
        }
    }

    private static Type[] DiscoverOptionsTypes()
        => [.. OptionsAssemblies
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => type is { IsClass: true, IsAbstract: false }
                                  && type.Name.EndsWith("Options", StringComparison.Ordinal)
                                  && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)];

    private static string[] ValidArguments() =>
    [
        "--environment=Production",
        "--ConnectionStrings:trellis-db=Host=localhost;Database=architecture_tests",
        "--ConnectionStrings:cache=localhost:6379",
        $"--Authentication:SigningKey={SigningKey}",
    ];

    [GeneratedRegex(@"\bclass\s+(?<name>\w+Options)\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OptionsClassDeclaration();

    [GeneratedRegex(@"\[\s*(?:Required|Range|MinLength|MaxLength|StringLength|Length|EmailAddress|Url|RegularExpression|EnumDataType|AllowedValues|DeniedValues)\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ValidationAttributeUsage();

    /// <summary>
    /// An application's real host — its own <c>Program</c>, its own registrations, its own configuration —
    /// built but never started, the way <c>WebApplicationFactory</c> does it: a host announces itself on
    /// the "Microsoft.Extensions.Hosting" <see cref="DiagnosticListener"/> once its container is built,
    /// and the observer keeps it and aborts <c>Main</c> right there with <see cref="HostAbortedException"/>.
    /// </summary>
    private sealed class BuiltHost : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IAsyncDisposable
    {
        // Listeners are process-wide; only the build this capture started (same async flow) is taken.
        private static readonly AsyncLocal<BuiltHost?> Current = new();

        private readonly List<IDisposable> _subscriptions = [];
        private IHost? _host;

        public IServiceProvider Services => _host!.Services;

        public static BuiltHost Of(Assembly entryAssembly, string[] args)
        {
            MethodInfo entryPoint = entryAssembly.EntryPoint
                ?? throw new InvalidOperationException($"{entryAssembly.GetName().Name} has no entry point.");

            var capture = new BuiltHost();
            Current.Value = capture;
            capture._subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(capture));
            try
            {
                entryPoint.Invoke(null, [args]);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is HostAbortedException)
            {
                // Expected: thrown by OnNext below once the host was built.
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            }
            finally
            {
                Current.Value = null;
                lock (capture._subscriptions)
                {
                    capture._subscriptions.ForEach(static subscription => subscription.Dispose());
                }
            }

            return capture._host is not null
                ? capture
                : throw new InvalidOperationException($"{entryAssembly.GetName().Name}'s Main returned without building a host.");
        }

        public void OnNext(DiagnosticListener value)
        {
            if (string.Equals(value.Name, "Microsoft.Extensions.Hosting", StringComparison.Ordinal))
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(value.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (Current.Value == this
                && string.Equals(value.Key, "HostBuilt", StringComparison.Ordinal)
                && value.Value is IHost host)
            {
                _host = host;
                throw new HostAbortedException();
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public async ValueTask DisposeAsync()
        {
            if (_host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                _host?.Dispose();
            }
        }
    }
}
