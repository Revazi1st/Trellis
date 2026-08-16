using System.Diagnostics.Metrics;

namespace Trellis.Application.Observability;

/// <summary>
/// Business metrics for the Invoices feature. Counters are incremented by domain-event handlers,
/// which run from the transactional outbox — so they only ever count <b>committed</b> events.
/// The meter is registered with OpenTelemetry in ServiceDefaults.
/// </summary>
public sealed class InvoiceMetrics
{
    public const string MeterName = "Trellis.Invoices";

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

    public void InvoiceIssued(string currency) => _issued.Add(1, KeyValuePair.Create<string, object?>("currency", currency));

    public void InvoicePaid() => _paid.Add(1);
}
