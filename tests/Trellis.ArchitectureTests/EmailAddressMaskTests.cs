using System.Globalization;
using System.Reflection;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// The two copies of the e-mail mask behave the same. Infrastructure and Persistence are peers (ADR-0002), so
/// each keeps its own: <c>EmailAddressMask.Mask</c> for the e-mail adapters, whose failure text the outbox
/// writes into its row, and the private <c>SecurityEventLog.MaskEmail</c> for the sign-in events. Each is
/// reached by name through the layer anchors, as the other fitness facts reach a layer, and one set of rows
/// holds both. The mask keeps the local part's first character as a reader sees it, a text element: never half
/// of a surrogate pair, which is no character at all, and a letter written with a combining mark stays that
/// letter.
/// </summary>
public sealed class EmailAddressMaskTests
{
    /// <summary>U+1F600, outside the Basic Multilingual Plane: two UTF-16 code units.</summary>
    private static readonly string Grinning = char.ConvertFromUtf32(0x1F600);

    /// <summary>U+1F469 U+200D U+1F4BB, a woman technologist: three code points, one character on screen.</summary>
    private static readonly string Technologist = char.ConvertFromUtf32(0x1F469) + "‍" + char.ConvertFromUtf32(0x1F4BB);

    /// <summary>Each copy by the layer that keeps it: its assembly, its type and its method, which takes the address and returns the mask.</summary>
    private static readonly Dictionary<string, (Assembly Assembly, string Type, string Method)> Copies = new(StringComparer.Ordinal)
    {
        [Architecture.Infrastructure] = (Architecture.InfrastructureAssembly, Architecture.Infrastructure + ".Email.EmailAddressMask", "Mask"),
        [Architecture.Persistence] = (Architecture.PersistenceAssembly, Architecture.Persistence + ".Identity.SecurityEventLog", "MaskEmail"),
    };

    private static readonly (string Address, string Masked)[] Rows =
    [
        ("jane.doe@example.com", "j***@example.com"),
        ("j@example.com", "j***@example.com"),
        (Grinning + "jane@example.com", Grinning + "***@example.com"),
        (Grinning + "@example.com", Grinning + "***@example.com"),
        (Technologist + "dev@example.com", Technologist + "***@example.com"),
        ("émile@example.com", "é***@example.com"),   // é decomposed: the letter keeps its accent
        ("؀@example.com", "؀***@example.com"),          // a prepended mark joins what follows: not the @
        ("jane.doe", "***"),
        ("", "***"),
        ("@example.com", "***"),
        ("jane.doe@", "***"),
    ];

    public static TheoryData<string, string, string> EveryCopyAndRow()
    {
        var data = new TheoryData<string, string, string>();
        foreach (string copy in Copies.Keys)
        {
            foreach ((string address, string masked) in Rows)
            {
                data.Add(copy, address, masked);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCopyAndRow))]
    public void Each_copy_keeps_the_first_character_whole_and_hides_the_rest_of_the_local_part(string copy, string address, string expected)
    {
        string masked = Mask(copy)(address);

        UnpairedSurrogates(masked).ShouldBeEmpty();
        masked.ShouldBe(expected);
    }

    /// <summary>The copy's method as a delegate; a copy renamed or moved fails here, naming what was looked for.</summary>
    private static Func<string, string> Mask(string copy)
    {
        (Assembly assembly, string typeName, string methodName) = Copies[copy];
        Type type = assembly.GetType(typeName, throwOnError: true)!;
        MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(string)])
            .ShouldNotBeNull($"{typeName}.{methodName}(string) is the {copy} copy of the mask; if it moved, point {nameof(Copies)} at it");
        return method.CreateDelegate<Func<string, string>>();
    }

    /// <summary>Each surrogate that is not half of a valid pair (a high one before a low one), as <c>U+D83D at 0</c>.</summary>
    private static List<string> UnpairedSurrogates(string text)
        => [.. Enumerable.Range(0, text.Length)
            .Where(i => char.IsSurrogate(text[i]) && !char.IsSurrogatePair(text, i) && (i == 0 || !char.IsSurrogatePair(text, i - 1)))
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"U+{(int)text[i]:X4} at {i}"))];
}
