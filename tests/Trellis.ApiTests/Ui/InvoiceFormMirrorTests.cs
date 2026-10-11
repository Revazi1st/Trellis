#if (IncludeUI)
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Shouldly;
using Trellis.Domain.Common;
using Trellis.Domain.Invoices;
using Trellis.UI.Common.Forms;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// The invoice forms restate the Domain's length constants, because UI.Common references only the
/// contracts. This keeps the restatement honest: change a constant and the form that mirrors it fails
/// here, instead of letting the browser accept what the API then refuses (or refuse what it accepts).
/// </summary>
public sealed class InvoiceFormMirrorTests
{
    [Fact]
    public void Customer_name_is_capped_at_the_domain_length()
        => MaxLengthOf<CreateInvoiceForm>(nameof(CreateInvoiceForm.CustomerName)).ShouldBe(Invoice.CustomerNameMaxLength);

    [Fact]
    public void Customer_email_is_capped_at_the_domain_length()
        => MaxLengthOf<CreateInvoiceForm>(nameof(CreateInvoiceForm.CustomerEmail)).ShouldBe(EmailAddress.MaxLength);

    [Fact]
    public void Currency_is_exactly_the_domain_code_length()
    {
        StringLengthAttribute length = Attribute<CreateInvoiceForm, StringLengthAttribute>(nameof(CreateInvoiceForm.Currency));

        length.MaximumLength.ShouldBe(Currency.CodeLength);
        length.MinimumLength.ShouldBe(Currency.CodeLength);
    }

    [Fact]
    public void Line_description_is_capped_at_the_domain_length()
        => MaxLengthOf<InvoiceLineForm>(nameof(InvoiceLineForm.Description)).ShouldBe(InvoiceLine.DescriptionMaxLength);

    private static int MaxLengthOf<TForm>(string property) => Attribute<TForm, MaxLengthAttribute>(property).Length;

    private static TAttribute Attribute<TForm, TAttribute>(string property)
        where TAttribute : Attribute
        => typeof(TForm).GetProperty(property).ShouldNotBeNull($"{typeof(TForm).Name}.{property}")
            .GetCustomAttribute<TAttribute>().ShouldNotBeNull($"{typeof(TForm).Name}.{property} has no {typeof(TAttribute).Name}");
}
#endif
