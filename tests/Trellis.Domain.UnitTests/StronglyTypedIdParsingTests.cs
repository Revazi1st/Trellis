using System.Reflection;
using Shouldly;
using Trellis.Domain.Common;
using Trellis.SharedKernel.Domain;
using Xunit;

namespace Trellis.Domain.UnitTests;

/// <summary>
/// <c>IParsable</c> is what binds <c>{id}</c> route values, so Parse/TryParse are the IDs' public input
/// surface: they must round-trip what ToString prints and turn anything else down. Every
/// <see cref="IStronglyTypedId{TSelf}"/> in the Domain is a row, found by reflection, so a hand-copied
/// <c>TryParse</c> in a new feature's ID is checked without anyone adding one.
/// </summary>
public sealed class StronglyTypedIdParsingTests
{
    /// <summary>Text that is not a <see cref="Guid"/>, which every ID's <c>TryParse</c> turns down.</summary>
    private static readonly string?[] NotAGuid = [null, "", "   ", "not-a-guid", "1234", "00000000-0000-0000-0000-00000000000Z"];

    /// <summary>
    /// Every strongly-typed ID the Domain declares, from the assembly of a type no feature owns (remove-feature
    /// keeps <see cref="EmailAddress"/>); none at all once the last feature is gone, when the theory skips.
    /// </summary>
    public static TheoryData<Type> DomainIds { get; } =
        [.. typeof(EmailAddress).Assembly.GetTypes().Where(IsStronglyTypedId).OrderBy(static type => type.FullName, StringComparer.Ordinal)];

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(DomainIds))]
    public void An_id_mints_a_version_7_guid_round_trips_and_turns_down_text_that_is_not_a_guid(Type id)
    {
        id.GetInterfaces().ShouldContain(typeof(IParsable<>).MakeGenericType(id), $"{id.Name} binds from a route value only as IParsable<{id.Name}>");
        typeof(StronglyTypedIdParsingTests).GetMethod(nameof(ParsesLikeAGuid), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(id)
            .Invoke(null, BindingFlags.DoNotWrapExceptions, binder: null, parameters: null, culture: null);
    }

    /// <summary>
    /// Positive control: an ID shaped like every other (a version-7 <c>New()</c>, a round trip) whose
    /// <c>TryParse</c> takes any text is refused by the same check, at the <c>TryParse</c> it got wrong.
    /// </summary>
    [Fact]
    public void The_parsing_check_refuses_an_id_that_accepts_text_that_is_not_a_guid()
    {
        LenientId.New().Value.Version.ShouldBe(7);

        Should.Throw<ShouldAssertException>(ParsesLikeAGuid<LenientId>).Message.ShouldContain($"{nameof(LenientId)}.TryParse(");
    }

    private static bool IsStronglyTypedId(Type type)
        => type is { IsValueType: true, IsGenericTypeDefinition: false }
           && type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>) && i.GetGenericArguments()[0] == type);

    private static void ParsesLikeAGuid<TId>()
        where TId : struct, IStronglyTypedId<TId>, IParsable<TId>
    {
        // New() is the one way an id is minted (domain.md): a version-7 Guid, ordered by time.
        TId id = (TId)typeof(TId).GetMethod("New", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)
            .ShouldNotBeNull($"{typeof(TId).Name} has no static New()")
            .Invoke(null, null)!;
        id.Value.Version.ShouldBe(7, $"{typeof(TId).Name}.New()");

        string text = id.ToString()!;
        text.ShouldBe(id.Value.ToString());
        TId.Parse(text, provider: null).ShouldBe(id);
        TId.TryParse(text, provider: null, out TId parsed).ShouldBeTrue();
        parsed.ShouldBe(id);
        TId.From(id.Value).ShouldBe(id);

        foreach (string? notAGuid in NotAGuid)
        {
            TId.TryParse(notAGuid, provider: null, out TId refused).ShouldBeFalse($"{typeof(TId).Name}.TryParse(\"{notAGuid}\")");
            refused.Value.ShouldBe(Guid.Empty);
        }

        // The IParsable contract: Parse throws, TryParse reports. Route binding uses TryParse (→ 400).
        Should.Throw<FormatException>(() => TId.Parse("not-a-guid", provider: null));
    }

    /// <summary>The planted violation: shaped like every ID, except that <c>TryParse</c> takes any text.</summary>
    private readonly record struct LenientId(Guid Value) : IStronglyTypedId<LenientId>, IParsable<LenientId>
    {
        public static LenientId New() => new(Guid.CreateVersion7());

        public static LenientId From(Guid value) => new(value);

        public static LenientId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

        public static bool TryParse(string? s, IFormatProvider? provider, out LenientId result)
        {
            result = new LenientId(Guid.TryParse(s, provider, out Guid value) ? value : Guid.Empty);
            return true;
        }

        public override string ToString() => Value.ToString();
    }
}
