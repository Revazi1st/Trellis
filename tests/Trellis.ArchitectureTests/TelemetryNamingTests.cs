using System.Globalization;
using System.Text.RegularExpressions;
using Shouldly;
using Trellis.Api.Common.Http;
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
/// semantic-convention name used with its convention meaning (<see cref="SemanticConventionAttributes"/>);</item>
/// <item>no log message names the property of the caller's log scope
/// (<see cref="UserTelemetryMiddleware.ScopeProperty"/>), which every record of a signed-in request already
/// carries.</item>
/// </list>
/// A name passed as an identifier must be a <c>const string</c> declared in the same file (or in the file
/// named after its qualifier, <c>Type.Name</c> → <c>Type.cs</c>); anything the scan cannot resolve fails.
/// <para>
/// The same scan holds the operators' reference to the code: the <c>Telemetry reference</c> section of
/// <c>docs/deployment.md</c> lists every source, meter, instrument (with its unit), attribute and log event
/// id, and a name, unit or id found on one side only fails here, whichever side it is on.
/// </para>
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
        "enduser.id",   // the authenticated caller an operation runs for; user.id is the generic user (an OS account, say)
        "error.type",
        "exception.message",
        "exception.stacktrace",
        "exception.type",
        "service.version",
    };

    /// <summary>
    /// Names that break the rules today and are renamed by another change, each with its owner. Every entry
    /// must still occur: once the rename lands, the stale entry fails <see cref="Pending_renames_still_exist"/>
    /// and is deleted, so this list only ever shrinks. Empty since the use-case span attributes took the
    /// <c>trellis.usecase.</c> prefix (ADR-0028's 2026-10-11 amendment); keep it that way.
    /// </summary>
    private static readonly Dictionary<string, string> PendingRenames = new(StringComparer.Ordinal);

    private static readonly string Prefix = ServiceDefaults.Extensions.TelemetryPrefix;

    private static readonly string LowerPrefix = Prefix.ToLowerInvariant();

    private static readonly Lazy<Scan> Found = new(ScanSources);

    /// <summary>The heading of the section in <see cref="ReferenceDocument"/> that lists every telemetry name.</summary>
    private const string ReferenceHeading = "### Telemetry reference";

    private static readonly string ReferenceDocument = Path.Combine("docs", "deployment.md");

    private static readonly Lazy<Reference> Documented = new(ReadReference);

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

    /// <summary>
    /// A signed-in request's records carry the caller's user id in a log scope. A message template naming the
    /// same property would give one record two values under one key, and they need not be the same account
    /// (an administrator acting on someone else): an OTLP exporter writes both attributes, Serilog keeps the
    /// message's and drops the caller's. A message names the account it is about <c>{UserId}</c>, as the
    /// security events do, so the scope's name is one no message uses.
    /// </summary>
    [Fact]
    public void No_log_message_names_the_callers_scope_property()
        => Found.Value.CallerScopeMessages.ShouldBeEmpty(
            $"'{UserTelemetryMiddleware.ScopeProperty}' is the caller's log scope (UserTelemetryMiddleware): name the account a message is about "
            + "{UserId}, or give the scope a name no message uses, so a record never carries two user ids under one key");

    /// <summary>
    /// Positive control for <see cref="No_log_message_names_the_callers_scope_property"/>, a scan that expects
    /// zero hits: a template naming the scope's property (<c>NAME</c> below) is found in a plain or a verbatim
    /// literal, with a destructuring or format specifier too; an interpolated string's hole is not, and neither
    /// is another property or the bare name.
    /// </summary>
    [Theory]
    [InlineData("""logger.LogWarning("User {NAME} locked out")""", true)]
    [InlineData("""Message = "Changed by {@NAME}")]""", true)]
    [InlineData("""logger.LogInformation("{NAME,12:D}")""", true)]
    [InlineData("""logger.LogWarning(@"User {NAME} locked out")""", true)]
    [InlineData("""logger.LogWarning("User {UserId} locked out")""", false)]
    [InlineData("""logger.LogWarning($"User {NAME} locked out")""", false)]
    [InlineData("""logger.LogWarning($@"User {NAME} locked out")""", false)]
    [InlineData("""logger.LogWarning(@$"User {NAME} locked out")""", false)]
    [InlineData("""scope[ScopeProperty] = "NAME";""", false)]
    public void The_caller_scope_scan_tells_a_template_from_an_interpolation(string code, bool flagged)
        => CallerScopePlaceholder()
            .IsMatch(code.Replace("NAME", UserTelemetryMiddleware.ScopeProperty, StringComparison.Ordinal))
            .ShouldBe(flagged);

    [Fact]
    public void Every_emitted_name_is_in_the_telemetry_reference()
    {
        Documented.Value.Names.ShouldNotBeEmpty($"no telemetry name found under '{ReferenceHeading}' in {ReferenceDocument}");

        string[] undocumented = [.. EmittedNames()
            .Where(static name => !Documented.Value.Names.Contains(name.Value))
            .Select(static name => $"{name.Location}: '{name.Value}'")
            .Distinct(StringComparer.Ordinal)];

        undocumented.ShouldBeEmpty(
            $"list every emitted name under '{ReferenceHeading}' in {ReferenceDocument}: dashboards and alerts are built from that page");
    }

    [Fact]
    public void Every_name_in_the_telemetry_reference_is_emitted()
    {
        HashSet<string> emitted = new(EmittedNames().Select(static name => name.Value), StringComparer.Ordinal);

        string[] stale = [.. Documented.Value.Names.Where(name => !emitted.Contains(name)).Order(StringComparer.Ordinal)];

        stale.ShouldBeEmpty($"{ReferenceDocument} lists names the code no longer emits: rename or delete them there");
    }

    [Fact]
    public void Every_instrument_is_in_the_telemetry_reference_with_its_unit()
    {
        List<string> violations = [];
        foreach (Instrument instrument in Found.Value.Instruments)
        {
            if (!Documented.Value.Units.TryGetValue(instrument.Name.Value, out string? documented))
            {
                violations.Add($"{instrument.Name.Location}: '{instrument.Name.Value}' has no row in the metrics table");
            }
            else if (!string.Equals(documented, instrument.Unit, StringComparison.Ordinal))
            {
                violations.Add(
                    $"{instrument.Name.Location}: '{instrument.Name.Value}' is documented in '{documented}' but declares '{instrument.Unit ?? "no unit"}'");
            }
        }

        violations.ShouldBeEmpty($"every instrument has a metrics row under '{ReferenceHeading}' whose Unit cell is the unit it declares");
    }

    [Fact]
    public void The_telemetry_reference_lists_exactly_the_logged_event_ids()
    {
        Found.Value.EventIds.ShouldNotBeEmpty("the scan found no [LoggerMessage] event id; has the declaration pattern changed?");
        Documented.Value.EventIds.ShouldNotBeEmpty(
            $"no event-id row (a first cell such as '13xx') under '{ReferenceHeading}' in {ReferenceDocument}");

        HashSet<int> logged = [.. Found.Value.EventIds.Select(static site => site.Id)];
        string[] undocumented = [.. Found.Value.EventIds
            .Where(static site => !Documented.Value.EventIds.Contains(site.Id))
            .Select(static site => $"{site.Location}: {site.Id}")];
        int[] stale = [.. Documented.Value.EventIds.Where(id => !logged.Contains(id)).Order()];

        undocumented.ShouldBeEmpty($"list every event id in the event-id table under '{ReferenceHeading}' (a range such as 1301–1309 counts)");
        stale.ShouldBeEmpty($"{ReferenceDocument} lists event ids that no [LoggerMessage] declares");
    }

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
        var constants = new SourceConstants(files);

        foreach (SourceFile file in files)
        {
            string? Resolve(Match match)
            {
                string argument = match.Groups["arg"].Value;
                string? value = constants.Resolve(file, argument);
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
                List<string> arguments = SourceConstants.ArgumentsAt(file.Code, instrument.Index + instrument.Length - 1);
                if (NameOf(instrument) is { } name)
                {
                    // CreateCounter<T>(name, unit, description, …), CreateObservableGauge<T>(name, callback,
                    // unit, …): the unit is named, or positional right after the name (after the callback).
                    int unitPosition = instrument.Groups["kind"].Value.StartsWith("Observable", StringComparison.Ordinal) ? 2 : 1;
                    string? unitArgument = arguments.Skip(1).FirstOrDefault(static a => a.StartsWith("unit:", StringComparison.Ordinal))?["unit:".Length..].Trim();
                    if (unitArgument is null && arguments.Count > unitPosition && !NamedArgument().IsMatch(arguments[unitPosition]))
                    {
                        unitArgument = arguments[unitPosition];
                    }

                    string? unit = unitArgument is null ? null : constants.Resolve(file, unitArgument) ?? unitArgument;
                    scan.Instruments.Add(new Instrument(name, HasUnit: unitArgument is not null, unit));
                }
            }

            scan.EventIds.AddRange(LoggerMessageEventId().Matches(file.Code)
                .Select(match => new EventIdSite(
                    int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture),
                    $"{file.RelativePath}:{file.LineOf(match.Index)}")));

            scan.CallerScopeMessages.AddRange(CallerScopePlaceholder().Matches(file.Code)
                .Select(match => $"{file.RelativePath}:{file.LineOf(match.Index)}: {match.Value}"));
        }

        return scan;
    }

    /// <summary>Every emitted name: meters, sources, instruments and attributes, semantic-convention ones included.</summary>
    private static IEnumerable<Name> EmittedNames()
        => Found.Value.Meters
            .Concat(Found.Value.Sources)
            .Concat(Found.Value.Instruments.Select(static instrument => instrument.Name))
            .Concat(Found.Value.Attributes);

    /// <summary>
    /// Reads the reference section: every backticked name that carries the prefix or is a semantic-convention
    /// attribute, the Unit cell of each metrics row (keyed by the instrument in its first cell), and every
    /// event id of the rows whose first cell is a range such as <c>13xx</c> (one id, or an en-dash range like
    /// <c>1301–1309</c>). A missing section reads as empty, which the facts above report.
    /// </summary>
    private static Reference ReadReference()
    {
        var reference = new Reference();
        string[] lines = File.ReadAllLines(Path.Combine(RepositorySource.Root.FullName, ReferenceDocument));
        int start = Array.FindIndex(lines, static line => string.Equals(line.Trim(), ReferenceHeading, StringComparison.Ordinal));
        if (start < 0)
        {
            return reference;
        }

        var prefixedName = new Regex(
            $@"^(?:{Regex.Escape(Prefix)}|{Regex.Escape(LowerPrefix)})\.[A-Za-z0-9_.]+$", RegexOptions.None, TimeSpan.FromSeconds(1));
        bool IsName(string token) => prefixedName.IsMatch(token) || SemanticConventionAttributes.Contains(token);

        foreach (string line in lines.Skip(start + 1).TakeWhile(static line => !SectionEnd().IsMatch(line)))
        {
            reference.Names.UnionWith(CodeSpan().Matches(line).Select(static code => code.Groups["code"].Value).Where(IsName));

            if (!line.TrimStart().StartsWith('|'))
            {
                continue;
            }

            string[] cells = line.Trim().Trim('|').Split('|');
            string first = cells[0].Trim();
            if (EventIdRange().IsMatch(first))
            {
                foreach (Match span in EventIdSpan().Matches(string.Join('|', cells.Skip(1))))
                {
                    int from = int.Parse(span.Groups["from"].Value, CultureInfo.InvariantCulture);
                    int to = span.Groups["to"].Success ? int.Parse(span.Groups["to"].Value, CultureInfo.InvariantCulture) : from;
                    reference.EventIds.UnionWith(Enumerable.Range(from, Math.Max(to - from + 1, 1)));
                }
            }
            else if (cells.Length >= 4 && CodeSpan().Match(first) is { Success: true } instrument && prefixedName.IsMatch(instrument.Groups["code"].Value))
            {
                reference.Units[instrument.Groups["code"].Value] = cells[3].Trim().Trim('`');
            }
        }

        return reference;
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

    [GeneratedRegex(@"^[A-Za-z_]\w*\s*:(?!:)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamedArgument();

    [GeneratedRegex(@"\[\s*LoggerMessage\s*\(\s*EventId\s*=\s*(?<id>\d+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LoggerMessageEventId();

    /// <summary>
    /// A message-template placeholder for the caller's scope property (<c>{Name}</c>, <c>{@Name}</c>,
    /// <c>{Name:format}</c>) in a string literal that is not interpolated: in <c>$"…{Name}…"</c> it is a C# hole.
    /// </summary>
    [GeneratedRegex(@"(?<!\$)(?<!\$@)""[^""\n]*\{[@$]?" + UserTelemetryMiddleware.ScopeProperty + @"(?:[,:][^}""\n]*)?\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CallerScopePlaceholder();

    /// <summary>The next heading of the same or a higher level ends the reference section.</summary>
    [GeneratedRegex(@"^#{1,3}\s", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SectionEnd();

    [GeneratedRegex("`(?<code>[^`]+)`", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"^\d{2}xx$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EventIdRange();

    [GeneratedRegex(@"\b(?<from>\d{4})(?:\s*[–-]\s*(?<to>\d{4}))?\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EventIdSpan();

    private sealed record Name(string Value, string Location);

    /// <param name="Name">The instrument's name and where it is created.</param>
    /// <param name="HasUnit">Whether the creation passes a unit at all.</param>
    /// <param name="Unit">The unit as declared (a literal, or the constant it names); null when none is passed.</param>
    private sealed record Instrument(Name Name, bool HasUnit, string? Unit);

    private sealed record EventIdSite(int Id, string Location);

    /// <summary>What the reference section of <see cref="ReferenceDocument"/> lists.</summary>
    private sealed class Reference
    {
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Units { get; } = new(StringComparer.Ordinal);

        public HashSet<int> EventIds { get; } = [];
    }

    private sealed class Scan
    {
        public List<Name> Meters { get; } = [];

        public List<Name> Sources { get; } = [];

        public List<Instrument> Instruments { get; } = [];

        public List<Name> Attributes { get; } = [];

        public List<EventIdSite> EventIds { get; } = [];

        /// <summary>Each string literal whose template names the caller's scope property, with its location.</summary>
        public List<string> CallerScopeMessages { get; } = [];

        public List<string> Unresolved { get; } = [];
    }
}
