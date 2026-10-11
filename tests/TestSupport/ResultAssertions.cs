using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Shouldly;
using Trellis.SharedKernel.Results;

namespace Trellis.TestSupport;

/// <summary>
/// The success assertion for a <see cref="Result"/>, in Shouldly's style. A bare
/// <c>result.IsSuccess.ShouldBeTrue()</c> fails with "should be True but was False", and reading
/// <c>Value</c> on a failure names only the code; this names the error's code, type and description and
/// each field error a validation failure carries, so a red test says why without a debugger. Use it on an
/// arrange step's <c>Result</c> too: a set-up that silently failed otherwise surfaces later as a puzzling
/// assertion. A test about the failure asserts on <c>Error</c> instead.
/// One source file, compiled into every test project that sees the SharedKernel (each csproj links it).
/// </summary>
[ShouldlyMethods]
[DebuggerStepThrough]
internal static class ResultAssertions
{
    /// <summary>Fails, explaining the error, unless <paramref name="result"/> succeeded.</summary>
    public static void ShouldBeSuccess(
        this Result result,
        string? customMessage = null,
        [CallerArgumentExpression(nameof(result))] string expression = "result")
    {
        if (result.IsFailure)
        {
            throw new ShouldAssertException(Explain(expression, result.Error, customMessage));
        }
    }

    /// <summary>Fails, explaining the error, unless <paramref name="result"/> succeeded; returns its value.</summary>
    public static T ShouldBeSuccess<T>(
        this Result<T> result,
        string? customMessage = null,
        [CallerArgumentExpression(nameof(result))] string expression = "result")
    {
        ((Result)result).ShouldBeSuccess(customMessage, expression);
        return result.Value;
    }

    private static string Explain(string expression, Error error, string? customMessage)
    {
        var message = new StringBuilder()
            .AppendLine(expression)
            .Append("    should be a success but failed with ").Append(Describe(error));

        if (error is ValidationError validation)
        {
            foreach (Error child in validation.Errors)
            {
                message.AppendLine().Append("      - ").Append(Describe(child));
            }
        }

        if (!string.IsNullOrWhiteSpace(customMessage))
        {
            message.AppendLine().AppendLine().AppendLine("Additional Info:").Append("    ").Append(customMessage);
        }

        return message.ToString();
    }

    private static string Describe(Error error) => error is FieldError field
        ? $"{field.Field}: {field.Code} ({field.Type}): {field.Description}"
        : $"{error.Code} ({error.Type}): {error.Description}";
}
