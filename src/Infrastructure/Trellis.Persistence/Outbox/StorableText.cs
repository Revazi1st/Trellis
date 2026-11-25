using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Trellis.Persistence.Outbox;

/// <summary>
/// Text the database can store, made from text the processor does not control: a handler's exception message,
/// or a dead letter's reason, which can quote a parser's. The processor writes it into a row's <c>Error</c>,
/// and a save the database refuses for that text is bookkeeping that can never be written (ADR-0023): the
/// attempt is not counted, the row is neither backed off nor dead-lettered, and the message is dispatched again
/// each time its lease runs out, for ever. Two things a .NET string can hold are refused: a UTF-16 surrogate
/// that is not half of a valid pair, which UTF-8 cannot encode, and NUL (U+0000), which a text column does not
/// hold. Each becomes U+FFFD, the replacement character; valid pairs and every other character are kept, and
/// nothing is cut.
/// </summary>
internal static class StorableText
{
    private const char Replacement = '\uFFFD';

    /// <summary>
    /// <paramref name="text"/> itself when it is storable as it is, a missing text included (an exception type
    /// may hand back no message); otherwise a copy of the same length with each refused code unit replaced.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? From(string? text)
    {
        if (text is null)
        {
            return null;
        }

        char[]? storable = null;
        int index = 0;
        while (index < text.Length)
        {
            // Done with a scalar value, or InvalidData / NeedMoreData for a surrogate that is not half of a pair
            // (one code unit consumed).
            OperationStatus status = Rune.DecodeFromUtf16(text.AsSpan(index), out Rune rune, out int consumed);
            if (status != OperationStatus.Done || rune.Value == 0)
            {
                storable ??= text.ToCharArray();
                storable.AsSpan(index, consumed).Fill(Replacement);
            }

            index += consumed;
        }

        return storable is null ? text : new string(storable);
    }
}
