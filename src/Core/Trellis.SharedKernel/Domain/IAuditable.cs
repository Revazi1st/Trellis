namespace Trellis.SharedKernel.Domain;

/// <summary>
/// Audit columns stamped by the persistence AuditingInterceptor (TimeProvider + ICurrentUser); never set these by hand.
/// <see cref="CreatedBy"/> and <see cref="ModifiedBy"/> hold the acting user's id, or the name of a caller without one.
/// </summary>
public interface IAuditable
{
    DateTimeOffset CreatedAtUtc { get; set; }

    string? CreatedBy { get; set; }

    DateTimeOffset? ModifiedAtUtc { get; set; }

    string? ModifiedBy { get; set; }
}
