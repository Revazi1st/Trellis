using System.Reflection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// No security event takes text a caller typed straight into its line (CWE-117, <c>docs/threat-model.md</c>).
/// Every <c>[LoggerMessage]</c> method of Persistence's <c>SecurityEventLog</c> that takes a string is private,
/// reached only through a wrapper that puts the text on one line (<c>LoginFailedUnknownAccount</c> masks the
/// e-mail, then replaces each line ending and every other control character), or is listed here with the reason
/// no caller typed its string. The fact holds who can call an event, not what a wrapper does to the text: that
/// is held by the wrapper's own rows (1102's are in <c>AuthFlowTests</c>), and a new wrapper lands with rows of
/// its own. The class is internal, so it is reached by name through the layer anchor, as
/// <see cref="EmailAddressMaskTests"/> reaches its mask.
/// </summary>
public sealed class SecurityEventLogTests
{
    private const string SecurityEventLog = Architecture.Persistence + ".Identity.SecurityEventLog";

    private const string IdentityErrorCodes =
        "Identity's error codes, joined by IdentityService.CodesOf: IdentityErrorDescriber's fixed identifiers "
        + "(no describer of our own is registered), never text a caller typed";

    /// <summary>The events other code may call with a string no caller typed, as <c>Method(parameter)</c>, each with its reason.</summary>
    private static readonly Dictionary<string, string> StringsNoCallerTyped = new(StringComparer.Ordinal)
    {
        ["FailedSignInNotCounted(identityErrorCodes)"] = IdentityErrorCodes,
        ["FailedSignInCountNotReset(identityErrorCodes)"] = IdentityErrorCodes,
    };

    [Fact]
    public void Every_security_event_that_takes_a_string_is_private_behind_a_one_line_wrapper_or_listed_with_its_reason()
    {
        Type type = Architecture.PersistenceAssembly.GetType(SecurityEventLog, throwOnError: true)!;

        List<string> callable = StringParametersOtherCodeCanPass(type);

        callable.Except(StringsNoCallerTyped.Keys, StringComparer.Ordinal).ShouldBeEmpty(
            $"a security event that takes a string is private, reached through a wrapper that puts the text on one line "
            + $"(LoginFailedUnknownAccount), or is listed in {nameof(StringsNoCallerTyped)} with the reason no caller typed it");
        StringsNoCallerTyped.Keys.Except(callable, StringComparer.Ordinal).ShouldBeEmpty(
            $"listed in {nameof(StringsNoCallerTyped)}, but no event other code can call takes that string any more: remove the row");
    }

    [Fact]
    public void The_event_check_flags_a_public_or_internal_event_that_takes_a_string()
        => StringParametersOtherCodeCanPass(typeof(PlantedViolations.SecurityEvents))
            .ShouldBe(["Typed(text)", "TypedInTheAssembly(text)"]);

    /// <summary>
    /// Each string parameter of a <c>[LoggerMessage]</c> method that is not private, as <c>Method(parameter)</c>:
    /// a string other code in the assembly can hand the event as it was typed.
    /// </summary>
    private static List<string> StringParametersOtherCodeCanPass(Type type)
    {
        MethodInfo[] events = [.. type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static method => method.IsDefined(typeof(LoggerMessageAttribute), inherit: false))];
        events.ShouldNotBeEmpty($"{type.FullName} declares no [LoggerMessage] method; has the declaration pattern changed?");

        return [.. events
            .Where(static method => !method.IsPrivate)
            .SelectMany(static method => method.GetParameters()
                .Where(static parameter => parameter.ParameterType == typeof(string))
                .Select(parameter => $"{method.Name}({parameter.Name})"))
            .Order(StringComparer.Ordinal)];
    }
}
