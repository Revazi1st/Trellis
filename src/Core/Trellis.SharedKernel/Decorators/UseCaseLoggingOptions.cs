using System.ComponentModel.DataAnnotations;

namespace Trellis.SharedKernel.Decorators;

/// <summary>
/// What the use-case logging stage writes (<see cref="LoggingDecorators"/>), from the <c>Telemetry</c>
/// configuration section. A host that binds it validates it at boot and registers the bound instance as this
/// plain type, after or before <c>AddApplication()</c>, which otherwise registers the defaults.
/// </summary>
public sealed class UseCaseLoggingOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>
    /// Appends <see cref="Results.Error.Description"/> to the line of a failure that is not a
    /// <see cref="Results.ValidationError"/>. Off by default: a description can carry what a user typed or
    /// a third party wrote (Identity's "Email '…' is already taken."), and the outcome line is written at
    /// Information for every request. Turn it on in Development only (<c>Telemetry:IncludeErrorDetail</c>);
    /// everywhere else the error type and code identify the failure, and the response carries the text.
    /// </summary>
    public bool IncludeErrorDetail { get; init; }

    /// <summary>
    /// A use case that takes at least this long also writes a Warning (event 1007, <c>UseCaseSlow</c>) after
    /// its outcome line, naming the use case and its elapsed milliseconds, so one that degrades from 50 ms to
    /// 5 s reaches the Warning channel even without a metrics backend. Off (null) by default: a Warning means
    /// something unexpected happened (ADR-0028), and only an operator knows what slow means for a system
    /// (<c>Telemetry:SlowUseCaseThreshold</c>).
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "01:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Telemetry:SlowUseCaseThreshold must be between {1} and {2}.")]
    public TimeSpan? SlowUseCaseThreshold { get; init; }
}
