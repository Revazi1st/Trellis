namespace Trellis.SharedKernel.Decorators;

/// <summary>
/// What the use-case outcome line may carry (<see cref="LoggingDecorators"/>), from the <c>Telemetry</c>
/// configuration section. A host that binds it registers the bound instance as this plain type, after or
/// before <c>AddApplication()</c>, which otherwise registers the defaults.
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
}
