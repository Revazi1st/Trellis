using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Trellis.SharedKernel.Validation;

/// <summary>
/// A <see cref="TimeSpan"/> limit, inclusive at both ends, for a property bound from configuration:
/// <c>[TimeSpanRange("00:00:01", "1.00:00:00", ErrorMessage = "Outbox:MaxRetryDelay must be between {1} and {2}.")]</c>.
/// The bounds are written as the configuration writes a <see cref="TimeSpan"/>, <c>[-][d.]hh:mm:ss[.fffffff]</c>,
/// and parsed in the invariant culture whatever the thread's culture; the message's <c>{0}</c> is the member,
/// <c>{1}</c> and <c>{2}</c> the bounds, as <see cref="RangeAttribute"/>'s are.
/// <para>
/// Use it instead of <see cref="RangeAttribute"/> with a type argument. That one keeps its bounds as the two
/// strings it was given, converts them on its first use and writes the converted values back over them without
/// a lock, and an attribute instance is shared by every host in the process: two hosts that validate the same
/// options type for the first time at once (parallel test fixtures, each booting the API) can read a bound
/// half-converted, and one of them fails to start with an <see cref="InvalidCastException"/> or an
/// <see cref="ArgumentException"/> instead of validating (dotnet/runtime#1143 and #110917; the runtime keeps it).
/// This one parses both bounds in its constructor, so a malformed bound fails where the attribute is read, and
/// it holds nothing that changes afterwards. <c>OptionsConventionTests</c> fails on the other form under
/// <c>src/</c> and <c>tools/</c>.
/// </para>
/// <para>
/// A null value passes: whether one is required is <see cref="RequiredAttribute"/>'s business, as with
/// <see cref="RangeAttribute"/>. A value of any other type than <see cref="TimeSpan"/> fails.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class TimeSpanRangeAttribute : ValidationAttribute
{
    /// <summary>The message when no <see cref="ValidationAttribute.ErrorMessage"/> is given: <see cref="RangeAttribute"/>'s.</summary>
    private const string RangeMessage = "The field {0} must be between {1} and {2}.";

    /// <param name="minimum">The smallest value accepted, in the invariant culture (<c>00:00:01</c>).</param>
    /// <param name="maximum">The largest value accepted, in the invariant culture (<c>1.00:00:00</c>).</param>
    /// <exception cref="ArgumentException">A bound is not a <see cref="TimeSpan"/>, or the minimum is above the maximum.</exception>
    public TimeSpanRangeAttribute(string minimum, string maximum)
        : base(RangeMessage)
    {
        Minimum = TimeSpan.TryParse(minimum, CultureInfo.InvariantCulture, out TimeSpan parsedMinimum)
            ? parsedMinimum
            : throw new ArgumentException(NotATimeSpan("minimum", minimum), nameof(minimum));
        Maximum = TimeSpan.TryParse(maximum, CultureInfo.InvariantCulture, out TimeSpan parsedMaximum)
            ? parsedMaximum
            : throw new ArgumentException(NotATimeSpan("maximum", maximum), nameof(maximum));

        if (Minimum > Maximum)
        {
            throw new ArgumentException($"The minimum '{minimum}' is above the maximum '{maximum}'.", nameof(minimum));
        }
    }

    /// <summary>The smallest value accepted.</summary>
    public TimeSpan Minimum { get; }

    /// <summary>The largest value accepted.</summary>
    public TimeSpan Maximum { get; }

    /// <summary>True for null and for a <see cref="TimeSpan"/> within the bounds, both included; false otherwise.</summary>
    public override bool IsValid(object? value) => value switch
    {
        null => true,
        TimeSpan span => span >= Minimum && span <= Maximum,
        _ => false,
    };

    /// <summary>The message with <c>{0}</c> the member's name and <c>{1}</c>, <c>{2}</c> the bounds (<c>00:00:01</c>, <c>1.00:00:00</c>).</summary>
    public override string FormatErrorMessage(string name)
        => string.Format(CultureInfo.CurrentCulture, ErrorMessageString, name, Minimum, Maximum);

    private static string NotATimeSpan(string bound, string? text)
        => $"The {bound} '{text}' is not a TimeSpan in the invariant culture ([-][d.]hh:mm:ss[.fffffff], as 00:00:01 or 1.00:00:00).";
}
