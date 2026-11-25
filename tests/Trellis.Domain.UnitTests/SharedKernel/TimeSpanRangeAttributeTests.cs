using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using Shouldly;
using Trellis.SharedKernel.Validation;
using Xunit;

namespace Trellis.Domain.UnitTests.SharedKernel;

/// <summary>
/// <see cref="TimeSpanRangeAttribute"/>, the <see cref="TimeSpan"/> limit every options type states instead of
/// <see cref="RangeAttribute"/> with a type argument, whose first use is not thread-safe. Its answers and its
/// messages are the old attribute's, so a configuration value that started the host before starts it now and
/// one that stopped it stops it with the same words.
/// </summary>
public sealed class TimeSpanRangeAttributeTests
{
    /// <summary>The bounds the options under <c>src/</c> state, each as written there.</summary>
    public static TheoryData<string, string> OptionsBounds() => new()
    {
        { "00:00:00.001", "01:00:00" },
        { "00:00:00.100", "00:10:00" },
        { "00:00:00", "01:00:00" },
        { "00:00:01", "01:00:00" },
        { "00:00:01", "00:10:00" },
        { "00:00:01", "1.00:00:00" },
        { "01:00:00", "365.00:00:00" },
        { "1.00:00:00", "365.00:00:00" },
    };

    [Theory]
    [MemberData(nameof(OptionsBounds))]
    public void Both_bounds_pass_and_one_tick_outside_either_fails(string minimum, string maximum)
    {
        var range = new TimeSpanRangeAttribute(minimum, maximum);
        TimeSpan lowest = TimeSpan.Parse(minimum, CultureInfo.InvariantCulture);
        TimeSpan highest = TimeSpan.Parse(maximum, CultureInfo.InvariantCulture);

        range.IsValid(lowest).ShouldBeTrue();
        range.IsValid(highest).ShouldBeTrue();
        range.IsValid(lowest - TimeSpan.FromTicks(1)).ShouldBeFalse();
        range.IsValid(highest + TimeSpan.FromTicks(1)).ShouldBeFalse();
    }

    [Fact]
    public void A_null_value_passes_because_requiring_one_is_another_attributes_business()
        => new TimeSpanRangeAttribute("00:00:01", "01:00:00").IsValid(null).ShouldBeTrue();

    [Theory]
    [InlineData("00:00:05")]
    [InlineData(5)]
    [InlineData(5d)]
    [InlineData(5L)]
    public void A_value_that_is_not_a_TimeSpan_fails(object value)
        => new TimeSpanRangeAttribute("00:00:01", "01:00:00").IsValid(value).ShouldBeFalse();

    [Fact]
    public void The_default_message_names_the_member_and_both_bounds()
        => new TimeSpanRangeAttribute("00:00:00.100", "1.00:00:00").FormatErrorMessage("PollInterval")
            .ShouldBe("The field PollInterval must be between 00:00:00.1000000 and 1.00:00:00.");

    [Fact]
    public void A_message_that_names_the_configuration_key_gets_the_bounds()
        => new TimeSpanRangeAttribute("00:00:01", "1.00:00:00") { ErrorMessage = "Outbox:MaxRetryDelay must be between {1} and {2}." }
            .FormatErrorMessage(nameof(Knobs.MaxRetryDelay))
            .ShouldBe("Outbox:MaxRetryDelay must be between 00:00:01 and 1.00:00:00.");

    /// <summary>The path the options host takes: <c>ValidateDataAnnotations()</c> hands the bound instance to the BCL validator.</summary>
    [Fact]
    public void Through_the_validator_an_out_of_range_member_fails_with_its_message_and_an_absent_one_passes()
    {
        var options = new Knobs { MaxRetryDelay = TimeSpan.FromDays(2), SlowThreshold = null };
        List<ValidationResult> failures = [];

        Validator.TryValidateObject(options, new ValidationContext(options), failures, validateAllProperties: true).ShouldBeFalse();

        ValidationResult failure = failures.ShouldHaveSingleItem();
        failure.ErrorMessage.ShouldBe("Outbox:MaxRetryDelay must be between 00:00:01 and 1.00:00:00.");
        failure.MemberNames.ShouldHaveSingleItem().ShouldBe(nameof(Knobs.MaxRetryDelay));
    }

    /// <summary>
    /// Row by row, the attribute answers and words its messages as the one it replaces, written as the options
    /// wrote it. Built here on one thread, fresh for each row, the old attribute is safe: its race needs two
    /// threads on its first use, which is why only <c>src/</c> and <c>tools/</c> are scanned for it.
    /// </summary>
    [Theory]
    [MemberData(nameof(OptionsBounds))]
    public void It_answers_and_words_its_messages_as_the_RangeAttribute_it_replaces(string minimum, string maximum)
    {
        TimeSpan lowest = TimeSpan.Parse(minimum, CultureInfo.InvariantCulture);
        TimeSpan highest = TimeSpan.Parse(maximum, CultureInfo.InvariantCulture);
        TimeSpan?[] values = [null, lowest, highest, lowest - TimeSpan.FromTicks(1), highest + TimeSpan.FromTicks(1), TimeSpan.MinValue, TimeSpan.MaxValue];
        const string keyed = "Section:Key must be between {1} and {2}.";

        foreach (TimeSpan? value in values)
        {
            new TimeSpanRangeAttribute(minimum, maximum).IsValid(value)
                .ShouldBe(OldRange(minimum, maximum).IsValid(value), $"the answer for {value}");
        }

        new TimeSpanRangeAttribute(minimum, maximum).FormatErrorMessage("Key")
            .ShouldBe(OldRange(minimum, maximum).FormatErrorMessage("Key"));
        new TimeSpanRangeAttribute(minimum, maximum) { ErrorMessage = keyed }.FormatErrorMessage("Key")
            .ShouldBe(OldRange(minimum, maximum, keyed).FormatErrorMessage("Key"));
    }

    /// <summary>
    /// The bounds are the configuration's text and mean the same under every culture: the invariant form reads
    /// as itself, and a culture's own form (German's decimal comma, which a current-culture parse would take as
    /// 100 ms) is refused, so a bound never depends on the machine the host boots on. The message's bounds are
    /// the invariant form too.
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    [InlineData("")]
    public void The_bounds_are_read_in_the_invariant_culture_whatever_the_threads_culture(string cultureName)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;

            var range = new TimeSpanRangeAttribute("00:00:00.100", "1.00:00:00");

            range.Minimum.ShouldBe(TimeSpan.FromMilliseconds(100));
            range.Maximum.ShouldBe(TimeSpan.FromDays(1));
            range.IsValid(TimeSpan.FromMilliseconds(100)).ShouldBeTrue();
            range.IsValid(TimeSpan.FromMilliseconds(99)).ShouldBeFalse();
            range.FormatErrorMessage("PollInterval").ShouldBe("The field PollInterval must be between 00:00:00.1000000 and 1.00:00:00.");
            Should.Throw<ArgumentException>(() => new TimeSpanRangeAttribute("00:00:00,100", "1.00:00:00")).ParamName.ShouldBe("minimum");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    [Theory]
    [InlineData("1 day", "01:00:00", "minimum", "The minimum '1 day' is not a TimeSpan")]
    [InlineData("00:00:01", "00:60:00", "maximum", "The maximum '00:60:00' is not a TimeSpan")]
    [InlineData("00:00:01", "", "maximum", "The maximum '' is not a TimeSpan")]
    [InlineData("01:00:00", "00:00:01", "minimum", "The minimum '01:00:00' is above the maximum '00:00:01'")]
    public void A_malformed_bound_fails_at_construction_saying_which(string minimum, string maximum, string parameter, string message)
    {
        ArgumentException exception = Should.Throw<ArgumentException>(() => new TimeSpanRangeAttribute(minimum, maximum));

        exception.ParamName.ShouldBe(parameter);
        exception.Message.ShouldContain(message);
    }

    /// <summary>
    /// What this attribute exists for, held without racing it: everything it adds to <see cref="ValidationAttribute"/>
    /// is a <see cref="TimeSpan"/> fixed by its constructor, so two threads validating with one instance read the
    /// same bounds. A readonly field is not enough: <see cref="RangeAttribute"/>'s defect would hide as well behind
    /// a readonly reference to something that changes (an array converted on first use, a cache) or in a static,
    /// so every field the class declares, static or not, must be a readonly <see cref="TimeSpan"/> or a constant.
    /// The base class's lazily built message accessor is the same for every caller (built from what was set before
    /// the instance was handed out), which is <see cref="RangeAttribute"/>'s case too; its bounds were the race.
    /// </summary>
    [Fact]
    public void The_attribute_holds_no_state_that_changes_after_construction()
    {
        Type type = typeof(TimeSpanRangeAttribute);

        type.IsSealed.ShouldBeTrue("a subclass could add state of its own");
        type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .ShouldNotBeEmpty("the bounds are fields; reflection found none, so the check below would prove nothing");
        StateThatCanChange(type).ShouldBeEmpty("every field is a readonly TimeSpan or a constant, and no property of its own can be set");
    }

    /// <summary>
    /// Positive control for the fact above, which expects nothing: each planted class holds one kind of state
    /// that can change after construction, and the same reading names it.
    /// </summary>
    [Fact]
    public void The_reading_of_state_that_can_change_names_what_a_planted_class_holds()
    {
        // Each planted class is used as written, so that what the reading names below is really state it holds.
        new BoundsConvertedOnFirstUse("00:00:01", "01:00:00").Minimum.ShouldBe("00:00:01");
        BoundsInAStaticCache.Count.ShouldBe(0);
        new BoundsThatCanBeSet { Maximum = TimeSpan.FromHours(1) }.Maximum.ShouldBe(TimeSpan.FromHours(1));

        StateThatCanChange(typeof(BoundsConvertedOnFirstUse)).ShouldBe(["_bounds"]);
        StateThatCanChange(typeof(BoundsInAStaticCache)).ShouldBe(["Parsed"]);
        StateThatCanChange(typeof(BoundsThatCanBeSet)).Contains("Maximum", StringComparer.Ordinal).ShouldBeTrue("a property with a setter is state that can change");
    }

    /// <summary>
    /// The members <paramref name="type"/> declares that can hold something different later: a field, static or
    /// not, that is neither a constant nor a readonly <see cref="TimeSpan"/>, and a property with a setter.
    /// </summary>
    private static string[] StateThatCanChange(Type type)
    {
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        return
        [
            .. type.GetFields(Declared)
                .Where(static field => !field.IsLiteral && !(field.IsInitOnly && field.FieldType == typeof(TimeSpan)))
                .Select(static field => field.Name),
            .. type.GetProperties(Declared)
                .Where(static property => property.CanWrite)
                .Select(static property => property.Name),
        ];
    }

    /// <summary>The old attribute's shape behind a readonly reference: the array is fixed, what it holds is not.</summary>
    private sealed class BoundsConvertedOnFirstUse(string minimum, string maximum)
    {
        private readonly object[] _bounds = [minimum, maximum];

        public object Minimum => _bounds[0];
    }

    /// <summary>A cache every instance shares.</summary>
    private static class BoundsInAStaticCache
    {
        private static readonly Dictionary<string, TimeSpan> Parsed = new(StringComparer.Ordinal);

        public static int Count => Parsed.Count;
    }

    /// <summary>A bound anyone holding the instance can move.</summary>
    private sealed class BoundsThatCanBeSet
    {
        public TimeSpan Maximum { get; set; }
    }

    private static RangeAttribute OldRange(string minimum, string maximum, string? errorMessage = null)
    {
        var range = new RangeAttribute(typeof(TimeSpan), minimum, maximum) { ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true };
        if (errorMessage is not null)
        {
            range.ErrorMessage = errorMessage;
        }

        return range;
    }

    private sealed class Knobs
    {
        [TimeSpanRange("00:00:01", "1.00:00:00", ErrorMessage = "Outbox:MaxRetryDelay must be between {1} and {2}.")]
        public TimeSpan MaxRetryDelay { get; init; }

        [TimeSpanRange("00:00:00.001", "01:00:00", ErrorMessage = "Telemetry:SlowUseCaseThreshold must be between {1} and {2}.")]
        public TimeSpan? SlowThreshold { get; init; }
    }
}
