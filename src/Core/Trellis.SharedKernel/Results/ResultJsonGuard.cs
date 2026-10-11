using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trellis.SharedKernel.Results;

/// <summary>
/// Refuses to put a <see cref="Result"/> on the wire. An endpoint that returns <c>Task&lt;Result&lt;T&gt;&gt;</c>
/// instead of mapping it compiles, passes every gate and answers 200 with the Result's internals on success
/// and a 500 on failure (reading <c>Value</c> throws). Attached to both Result types, this factory makes the
/// first serialisation attempt throw instead, naming the fix: map the Result at the endpoint
/// (ADR-0004; the API's single translation point is <c>CustomResults.Problem</c>).
/// </summary>
internal sealed class ResultJsonGuard : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(Result).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException(
            $"'{typeToConvert}' is never serialised: a Result is mapped at the endpoint, never returned as the body. " +
            "Answer with TypedResults on success and CustomResults.Problem(result.Error) on failure.");
}
