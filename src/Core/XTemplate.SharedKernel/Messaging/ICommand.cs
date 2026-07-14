using System.Diagnostics.CodeAnalysis;

namespace XTemplate.SharedKernel.Messaging;

/// <summary>A state-changing use case with no return value beyond success/failure.</summary>
public interface ICommand;

/// <summary>A state-changing use case returning <typeparamref name="TResponse"/> (e.g. the created id).</summary>
[SuppressMessage("Major Code Smell", "S2326:Unused type parameters should be removed",
    Justification = "Phantom type parameter links the command to its handler's response type at compile time.")]
public interface ICommand<TResponse>;
