using System.Text;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// Telemetry names are a contract with every dashboard and alert built on them, and the subscription
/// depends on them: ServiceDefaults exports every meter and trace source whose name starts with
/// <see cref="ServiceDefaults.Extensions.TelemetryPrefix"/>, so a meter named anything else is created,
/// incremented and never exported. A source scan over <c>src</c> and <c>tools</c> holds every name to it:
/// <list type="bullet">
/// <item>meter and <c>ActivitySource</c> names start with <c>Trellis.</c>;</item>
/// <item>instrument names start with <c>trellis.</c> and every instrument declares its unit;</item>
/// <item>a custom span or metric attribute starts with <c>trellis.</c>, unless it is an OpenTelemetry
/// semantic-convention name used with its convention meaning (<see cref="SemanticConventionAttributes"/>).</item>
/// </list>
/// A name passed as an identifier must be a <c>const string</c> declared in the same file (or in the file
/// named after its qualifier, <c>Type.Name</c> → <c>Type.cs</c>); anything the scan cannot resolve fails.
/// </summary>
public sealed partial class TelemetryNamingTests
{
    /// <summary>
    /// OpenTelemetry semantic-convention attribute names the solution may use as they are. Add one only
    /// when it carries its registered meaning (https://opentelemetry.io/docs/specs/semconv/registry/attributes/);
    /// a name of our own takes the prefix instead, so it can never collide with a future convention.
    /// </summary>
    private static readonly HashSet<string> SemanticConventionAttributes = new(StringComparer.Ordinal)
    {
        "deployment.environment.name",
        "error.type",
        "exception.message",
        "exception.stacktrace",
        "exception.type",
        "service.version",
    };

    /// <summary>
    /// Names that break the rules today and are renamed by another change, each with its owner. Every entry
    /// must still occur: once the rename lands, the stale entry fails <see cref="Pending_renames_still_exist"/>
    /// and is deleted, so this list only ever shrinks.
    /// </summary>
    private static readonly Dictionary<string, string> PendingRenames = new(StringComparer.Ordinal)
    {
        ["usecase.outcome"] = "use-case span attribute (SharedKernel TracingDecorators): becomes trellis.usecase.outcome",
        ["usecase.error_code"] = "use-case span attribute (SharedKernel TracingDecorators): becomes trellis.usecase.error_code",
        ["usecase.error_type"] = "use-case span attribute (SharedKernel TracingDecorators): becomes error.type or trellis.usecase.error_type",
    };

    private static readonly string Prefix = ServiceDefaults.Extensions.TelemetryPrefix;

    private static readonly string LowerPrefix = Prefix.ToLowerInvariant();

    private static readonly Lazy<Scan> Found = new(ScanSources);

    [Fact]
    public void Meters_and_activity_sources_are_named_with_the_subscribed_prefix()
    {
        Found.Value.Meters.ShouldNotBeEmpty("the scan found no Meter; has the creation pattern changed?");
        Found.Value.Sources.ShouldNotBeEmpty("the scan found no ActivitySource; has the creation pattern changed?");

        List<string> violations = [.. Found.Value.Meters.Concat(Found.Value.Sources)
            .Where(static name => !name.Value.StartsWith(Prefix + ".", StringComparison.Ordinal))
            .Select(static name => $"{name.Location}: '{name.Value}'")];

        violations.ShouldBeEmpty($"name every Meter and ActivitySource '{Prefix}.<Area>', or ServiceDefaults never subscribes it");
    }

    [Fact]
    public void Instruments_are_prefixed_and_declare_a_unit()
    {
        Found.Value.Instruments.ShouldNotBeEmpty("the scan found no instrument; has the creation pattern changed?");

        List<string> violations = [.. Found.Value.Instruments
            .Where(static instrument => !PendingRenames.ContainsKey(instrument.Name.Value))
            .Where(static instrument => !instrument.Name.Value.StartsWith(LowerPrefix + ".", StringComparison.Ordinal) || !instrument.HasUnit)
            .Select(static instrument => $"{instrument.Name.Location}: '{instrument.Name.Value}'{(instrument.HasUnit ? string.Empty : " has no unit")}")];

        violations.ShouldBeEmpty(
            $"instrument names start with '{LowerPrefix}.' and every instrument passes unit: (UCUM, e.g. \"s\", \"By\", or a {{annotation}} such as \"{{invoice}}\")");
    }

    [Fact]
    public void Custom_attributes_are_prefixed_or_semantic_convention_names()
    {
        Found.Value.Attributes.ShouldNotBeEmpty("the scan found no span or metric attribute; has the call pattern changed?");

        List<string> violations = [.. Found.Value.Attributes
            .Where(static attribute => !PendingRenames.ContainsKey(attribute.Value))
            .Where(static attribute => !attribute.Value.StartsWith(LowerPrefix + ".", StringComparison.Ordinal)
                                       && !SemanticConventionAttributes.Contains(attribute.Value))
            .Select(static attribute => $"{attribute.Location}: '{attribute.Value}'")];

        violations.ShouldBeEmpty(
            $"a custom attribute is named '{LowerPrefix}.<area>.<name>'; a semantic-convention name used with its meaning goes on SemanticConventionAttributes");
    }

    [Fact]
    public void Every_name_passed_as_an_identifier_resolves_to_a_constant()
        => Found.Value.Unresolved.ShouldBeEmpty(
            "pass telemetry names as string literals or as const strings declared in the same file (or in Type.cs for Type.Name)");

    [Fact]
    public void Pending_renames_still_exist()
    {
        HashSet<string> current = new(
            Found.Value.Attributes.Select(static a => a.Value)
                .Concat(Found.Value.Instruments.Where(static i => !i.HasUnit).Select(static i => i.Name.Value)),
            StringComparer.Ordinal);

        PendingRenames.Keys.Where(name => !current.Contains(name)).ShouldBeEmpty(
            "these names were renamed (or fixed): delete them from TelemetryNamingTests.PendingRenames");
    }

    private static Scan ScanSources()
    {
        var scan = new Scan();
        List<SourceFile> files = [.. RepositorySource.CSharpFiles("src", "tools")];
        Dictionary<string, SourceFile> byFileName = files
            .GroupBy(static f => Path.GetFileNameWithoutExtension(f.RelativePath), StringComparer.Ordinal)
            .Where(static g => g.Count() == 1)
            .ToDictionary(static g => g.Key, static g => g.Single(), StringComparer.Ordinal);

        foreach (SourceFile file in files)
        {
            string? Resolve(Match match)
            {
                string argument = match.Groups["arg"].Value;
                if (argument.StartsWith('"'))
                {
                    return argument[1..^1];
                }

                string[] parts = argument.Split('.');
                string? value = ConstantIn(file, parts[^1]);
                if (value is null && parts.Length > 1 && byFileName.TryGetValue(parts[^2], out SourceFile? declaring))
                {
                    value = ConstantIn(declaring, parts[^1]);
                }

                if (value is null)
                {
                    scan.Unresolved.Add($"{file.RelativePath}:{file.LineOf(match.Index)}: '{argument}'");
                }

                return value;
            }

            Name? NameOf(Match match) => Resolve(match) is { } value ? new Name(value, $"{file.RelativePath}:{file.LineOf(match.Index)}") : null;

            scan.Meters.AddRange(MeterCreation().Matches(file.Code).Select(NameOf).OfType<Name>());
            scan.Sources.AddRange(ActivitySourceCreation().Matches(file.Code).Select(NameOf).OfType<Name>());
            scan.Attributes.AddRange(AttributeSite().Matches(file.Code).Select(NameOf).OfType<Name>());

            foreach (Match instrument in InstrumentCreation().Matches(file.Code))
            {
                List<string> arguments = ArgumentsAt(file.Code, instrument.Index + instrument.Length - 1);
                if (NameOf(instrument) is { } name)
                {
                    // CreateCounter<T>(name, unit, description, …), CreateObservableGauge<T>(name, callback,
                    // unit, …): the unit is named, or positional right after the name (after the callback).
                    int unitPosition = instrument.Groups["kind"].Value.StartsWith("Observable", StringComparison.Ordinal) ? 2 : 1;
                    bool hasUnit = arguments.Skip(1).Any(static a => a.StartsWith("unit:", StringComparison.Ordinal))
                                   || (arguments.Count > unitPosition && !NamedArgument().IsMatch(arguments[unitPosition]));
                    scan.Instruments.Add(new Instrument(name, hasUnit));
                }
            }
        }

        return scan;
    }

    private static string? ConstantIn(SourceFile file, string identifier)
        => ConstantDeclaration().Matches(file.Code)
            .FirstOrDefault(m => string.Equals(m.Groups["name"].Value, identifier, StringComparison.Ordinal))
            ?.Groups["value"].Value;

    /// <summary>The top-level arguments of the call whose opening parenthesis is at <paramref name="open"/>, trimmed.</summary>
    private static List<string> ArgumentsAt(string code, int open)
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

    private const string Argument = """(?<arg>"(?:[^"\\]|\\.)*"|(?!new\b)[A-Za-z_][\w.]*)""";

    [GeneratedRegex(@"(?:\bnew\s+Meter\s*\(|\bMeter\s+\w+\s*=\s*new\s*\(|\b\w*[Mm]eterFactory\s*\.\s*Create\s*\()\s*" + Argument, RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MeterCreation();

    [GeneratedRegex(@"(?:\bnew\s+ActivitySource\s*\(|\bActivitySource\s+\w+\s*=\s*new\s*\()\s*" + Argument, RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ActivitySourceCreation();

    /// <summary>An instrument factory call; the match ends on its opening parenthesis, the name is captured ahead.</summary>
    [GeneratedRegex(@"\.\s*Create(?<kind>Counter|UpDownCounter|Histogram|Gauge|ObservableCounter|ObservableUpDownCounter|ObservableGauge)\s*<[^>()]*>\s*\((?=\s*" + Argument + ")", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InstrumentCreation();

    [GeneratedRegex(@"(?:\.\s*(?:SetTag|AddTag)\s*\(|\bKeyValuePair\s*\.\s*Create\s*(?:<[^>()]*>)?\s*\(|\bnew\s+KeyValuePair\s*<[^>()]*>\s*\()\s*" + Argument, RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AttributeSite();

    [GeneratedRegex("""\bconst\s+string\s+(?<name>\w+)\s*=\s*"(?<value>(?:[^"\\]|\\.)*)"\s*;""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConstantDeclaration();

    [GeneratedRegex(@"^[A-Za-z_]\w*\s*:(?!:)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamedArgument();

    private sealed record Name(string Value, string Location);

    private sealed record Instrument(Name Name, bool HasUnit);

    private sealed class Scan
    {
        public List<Name> Meters { get; } = [];

        public List<Name> Sources { get; } = [];

        public List<Instrument> Instruments { get; } = [];

        public List<Name> Attributes { get; } = [];

        public List<string> Unresolved { get; } = [];
    }
}
