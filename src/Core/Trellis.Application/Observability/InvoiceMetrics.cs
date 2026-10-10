using System.Diagnostics.Metrics;

namespace Trellis.Application.Observability;

/// <summary>
/// Business metrics for the Invoices feature. Counters are incremented by domain-event handlers,
/// which run from the transactional outbox — so they only ever count <b>committed</b> events.
/// Every host exports it with no registration of its own: ServiceDefaults subscribes to every meter
/// whose name starts with <c>Trellis.</c>.
/// </summary>
public sealed class InvoiceMetrics
{
    public const string MeterName = "Trellis.Invoices";

    /// <summary>The ISO-4217 currency of an issued invoice. Prefixed: a custom attribute never borrows a semantic-convention namespace.</summary>
    public const string CurrencyAttribute = "trellis.invoice.currency";

    private readonly Counter<long> _issued;
    private readonly Counter<long> _paid;

    public InvoiceMetrics(IMeterFactory meterFactory)
    {
        Meter meter = meterFactory.Create(MeterName);

        _issued = meter.CreateCounter<long>(
            "trellis.invoices.issued", unit: "{invoice}", description: "Invoices issued.");
        _paid = meter.CreateCounter<long>(
            "trellis.invoices.paid", unit: "{invoice}", description: "Invoices paid.");
    }

    public void InvoiceIssued(string currency) => _issued.Add(1, KeyValuePair.Create<string, object?>(CurrencyAttribute, currency));

    public void InvoicePaid() => _paid.Add(1);
}
