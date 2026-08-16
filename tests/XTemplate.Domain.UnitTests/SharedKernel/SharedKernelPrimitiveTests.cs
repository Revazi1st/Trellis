using System.Text.Json;
using Shouldly;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Domain;
using XTemplate.SharedKernel.Specifications;
using Xunit;

namespace XTemplate.Domain.UnitTests.SharedKernel;

/// <summary>The primitives every generated project inherits verbatim — they earn direct tests.</summary>
public sealed class EntityEqualityTests
{
    private sealed class TestEntity(InvoiceId id) : Entity<InvoiceId>(id);

    private sealed class OtherEntity(InvoiceId id) : Entity<InvoiceId>(id);

    [Fact]
    public void Same_type_and_id_are_equal_through_Equals_and_operators()
    {
        InvoiceId id = InvoiceId.New();
        var left = new TestEntity(id);
        var right = new TestEntity(id);

        left.Equals(right).ShouldBeTrue();
        (left == right).ShouldBeTrue();
        (left != right).ShouldBeFalse();
        left.GetHashCode().ShouldBe(right.GetHashCode());
    }

    [Fact]
    public void Different_ids_or_types_are_not_equal()
    {
        InvoiceId id = InvoiceId.New();
        (new TestEntity(id) == new TestEntity(InvoiceId.New())).ShouldBeFalse();
        new TestEntity(id).Equals(new OtherEntity(id)).ShouldBeFalse();
    }

    [Fact]
    public void Null_comparisons_behave()
    {
        TestEntity? nothing = null;
        var entity = new TestEntity(InvoiceId.New());

        (nothing == null).ShouldBeTrue();
        (entity == null).ShouldBeFalse();
        (null != entity).ShouldBeTrue();
    }
}

public sealed class PagedListTests
{
    [Theory]
    [InlineData(10, 3, 4)]    // 10 items, size 3 → 4 pages
    [InlineData(9, 3, 3)]
    [InlineData(0, 3, 0)]
    public void TotalPages_rounds_up(int totalCount, int pageSize, int expectedPages)
        => new PagedList<int>([], 1, pageSize, totalCount).TotalPages.ShouldBe(expectedPages);

    [Fact]
    public void Page_size_zero_never_divides_by_zero()
        => new PagedList<int>([], 1, 0, 10).TotalPages.ShouldBe(0);

    [Fact]
    public void Navigation_flags_reflect_position()
    {
        var middle = new PagedList<int>([1], 2, 1, 3);
        middle.HasPreviousPage.ShouldBeTrue();
        middle.HasNextPage.ShouldBeTrue();

        var last = new PagedList<int>([1], 3, 1, 3);
        last.HasNextPage.ShouldBeFalse();
    }

    [Fact]
    public void Map_preserves_paging_metadata()
    {
        PagedList<string> mapped = new PagedList<int>([1, 2], 2, 2, 5)
            .Map(static n => n.ToString(System.Globalization.CultureInfo.InvariantCulture));

        mapped.Items.ShouldBe(["1", "2"]);
        mapped.Page.ShouldBe(2);
        mapped.TotalCount.ShouldBe(5);
    }
}

public sealed class StronglyTypedIdJsonTests
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new StronglyTypedIdJsonConverterFactory());
        return options;
    }

    [Fact]
    public void Ids_round_trip_as_their_inner_guid_string()
    {
        InvoiceId id = InvoiceId.New();

        string json = JsonSerializer.Serialize(id, Options);
        json.ShouldBe($"\"{id.Value}\"");

        JsonSerializer.Deserialize<InvoiceId>(json, Options).ShouldBe(id);
    }

    [Fact]
    public void Ids_nested_in_records_round_trip()
    {
        var payload = new IdCarrier(InvoiceId.New(), "name");

        string json = JsonSerializer.Serialize(payload, Options);
        JsonSerializer.Deserialize<IdCarrier>(json, Options).ShouldBe(payload);
    }

    private sealed record IdCarrier(InvoiceId Id, string Name);
}
