namespace Trellis.SharedKernel.Domain;

/// <summary>Soft-deleted by the persistence SoftDeleteInterceptor (Remove becomes an update); filtered out by the "SoftDeleted" named query filter.</summary>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAtUtc { get; set; }
}
