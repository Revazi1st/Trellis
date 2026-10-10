using Shouldly;
using Xunit;
using ContractStatus = Trellis.Api.Contracts.Invoices.InvoiceStatus;
using DomainStatus = Trellis.Domain.Invoices.InvoiceStatus;

namespace Trellis.ArchitectureTests;

/// <summary>
/// <c>Api.Contracts</c> may not reference the Domain (ADR-0013), so an enum that crosses the wire exists
/// twice: the domain's and its Contracts mirror, which the OpenAPI document declares as a string enum
/// (ADR-0025). The API maps between them by name; this test is what makes a member added on one side
/// without the other a build failure instead of a 500 on the first invoice in the new state.
/// </summary>
public sealed class ContractMirrorTests
{
    [Fact]
    public void Contract_InvoiceStatus_mirrors_the_domain_enum_member_for_member()
    {
        Enum.GetNames<ContractStatus>().ShouldBe(Enum.GetNames<DomainStatus>());

        foreach (DomainStatus domain in Enum.GetValues<DomainStatus>())
        {
            Enum.Parse<ContractStatus>(domain.ToString()).ShouldBe((ContractStatus)(int)domain,
                $"{domain} has a different ordinal in the Contracts mirror");
        }
    }
}
