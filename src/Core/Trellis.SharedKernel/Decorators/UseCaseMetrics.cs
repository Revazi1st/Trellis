using System.Diagnostics.Metrics;
using Trellis.SharedKernel.Results;

namespace Trellis.SharedKernel.Decorators;

/// <summary>
/// The use-case pipeline's instruments, on the <see cref="MeterName"/> meter (ServiceDefaults exports
/// every <c>Trellis.*</c> meter). <see cref="TracingDecorators"/> records them at the outcome site, after
/// the handler and the commit, so a validation code a domain rule returns (<c>Invoice.DueDateInPast</c>)
/// counts beside the ones FluentValidation produces.
/// </summary>
public static class UseCaseMetrics
{
    public const string MeterName = "Trellis.UseCases";

    /// <summary>
    /// One per validation failure a use case answered with: each entry of a <see cref="ValidationError"/>,
    /// or the single validation <see cref="Error"/> a handler returned.
    /// </summary>
    public const string ValidationFailuresInstrument = "trellis.validation.failures";

    /// <summary>The use case, by its message type's name (the name of its span).</summary>
    public const string UseCaseAttribute = "trellis.usecase.name";

    /// <summary>The failed rule's code (<c>Validation.NotEmpty</c>, <c>Invoice.DueDateInPast</c>); never the field or the value.</summary>
    public const string ValidationCodeAttribute = "trellis.validation.code";

    private static readonly Meter UseCaseMeter = new(MeterName);

    private static readonly Counter<long> ValidationFailures = UseCaseMeter.CreateCounter<long>(
        ValidationFailuresInstrument, unit: "{failure}", description: "Validation failures use cases answered with, by rule code.");

    /// <summary>Counts the failures of a Validation-typed result; any other outcome records nothing.</summary>
    internal static void RecordOutcome(string useCase, Result result)
    {
        if (result.IsSuccess || result.Error.Type != ErrorType.Validation || !ValidationFailures.Enabled)
        {
            return;
        }

        IEnumerable<Error> failures = result.Error is ValidationError validation ? validation.Errors : [result.Error];
        foreach (Error failure in failures)
        {
            ValidationFailures.Add(
                1,
                KeyValuePair.Create<string, object?>(UseCaseAttribute, useCase),
                KeyValuePair.Create<string, object?>(ValidationCodeAttribute, failure.Code));
        }
    }
}
