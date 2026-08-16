using System.Diagnostics.CodeAnalysis;

namespace Trellis.SharedKernel.Messaging;

/// <summary>A read-only use case returning <typeparamref name="TResponse"/>. Queries never mutate state.</summary>
[SuppressMessage("Major Code Smell", "S2326:Unused type parameters should be removed",
    Justification = "Phantom type parameter links the query to its handler's response type at compile time.")]
public interface IQuery<TResponse>;
