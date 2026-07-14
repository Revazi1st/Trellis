namespace XTemplate.SharedKernel.Domain;

/// <summary>Audit columns stamped by the persistence AuditingInterceptor (TimeProvider + ICurrentUser); never set these by hand.</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAtUtc { get; set; }

    string? CreatedBy { get; set; }

    DateTimeOffset? ModifiedAtUtc { get; set; }

    string? ModifiedBy { get; set; }
}
