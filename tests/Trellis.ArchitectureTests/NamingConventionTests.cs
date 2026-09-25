using System.Globalization;
using System.Reflection;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// Naming and layout conventions. These are the rules a reader (or an agent) relies on to find
/// things without reading them first: if a namespace can drift from its folder, "where does this
/// live" stops having an answer, and the template's whole navigability argument goes with it.
/// </summary>
public sealed class NamingConventionTests
{
    private static readonly Assembly[] ProductionAssemblies =
    [
        Architecture.SharedKernelAssembly,
        Architecture.DomainAssembly,
        Architecture.ApplicationAssembly,
        Architecture.InfrastructureAssembly,
        Architecture.PersistenceAssembly,
        Architecture.ApiContractsAssembly,
    ];

    /// <summary>
    /// The SharedKernel is exempt on purpose. Its awaitable members are named after the patterns
    /// they implement, not after their return type: <c>ICommandHandler.Handle</c> is the canonical
    /// CQRS shape, and <c>Bind</c>/<c>Map</c>/<c>Tap</c> are the functional combinators the Result
    /// type is modelled on. Renaming those to satisfy a suffix rule would churn every handler,
    /// decorator and endpoint in the template to make the contracts read worse.
    /// </summary>
    private static readonly Assembly[] AsyncSuffixAssemblies =
    [
        Architecture.DomainAssembly,
        Architecture.ApplicationAssembly,
        Architecture.InfrastructureAssembly,
        Architecture.PersistenceAssembly,
    ];

    [Fact]
    public void Awaitable_methods_end_with_Async()
    {
        List<string> offenders = [];

        foreach (Assembly assembly in AsyncSuffixAssemblies)
        {
            IEnumerable<MethodInfo> methods = assembly.GetTypes()
                .Where(static type => !IsCompilerGenerated(type))
                .SelectMany(static type => type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Where(static method => !method.IsSpecialName && IsAwaitable(method.ReturnType));

            offenders.AddRange(methods
                .Where(static method => !method.Name.EndsWith("Async", StringComparison.Ordinal))
                // Interface implementations inherit their name from the interface; flagging both
                // ends of the same violation just doubles the noise.
                .Where(static method => !IsInterfaceImplementation(method))
                .Select(static method => $"{method.DeclaringType!.FullName}.{method.Name}"));
        }

        offenders.ShouldBeEmpty(
            $"awaitable methods must end in 'Async': {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Namespaces_mirror_the_folder_structure()
    {
        // Proven via the assembly's own root namespace prefix: every type must sit under the
        // assembly name, which is what the SDK derives folder-by-folder.
        List<string> offenders = [];

        foreach (Assembly assembly in ProductionAssemblies)
        {
            string root = assembly.GetName().Name!;

            offenders.AddRange(assembly.GetTypes()
                .Where(static type => type.IsPublic || type.IsNestedPublic || !type.IsNested)
                .Where(static type => !IsCompilerGenerated(type))
                .Where(type => type.Namespace is not null)
                .Where(type => !type.Namespace!.StartsWith(root, StringComparison.Ordinal))
                .Select(type => $"{type.FullName} (expected namespace under {root})"));
        }

        offenders.ShouldBeEmpty(
            $"namespaces must mirror the project/folder they live in: {string.Join(", ", offenders)}");
    }

    [Theory]
    [InlineData("Repository", "Repositories")]
    [InlineData("Validator", "Validators")]
    public void Types_in_a_role_carry_its_suffix(string suffix, string role)
    {
        List<string> offenders = [];

        foreach (Assembly assembly in ProductionAssemblies)
        {
            offenders.AddRange(assembly.GetTypes()
                .Where(static type => type is { IsClass: true, IsAbstract: false })
                .Where(static type => !IsCompilerGenerated(type))
                .Where(type => BelongsToRole(type, suffix))
                .Where(type => !type.Name.EndsWith(suffix, StringComparison.Ordinal))
                .Select(type => type.FullName!));
        }

        offenders.ShouldBeEmpty(
            string.Create(CultureInfo.InvariantCulture, $"{role} must be named with the '{suffix}' suffix: {string.Join(", ", offenders)}"));
    }

    private static bool BelongsToRole(Type type, string suffix) => suffix switch
    {
        // A repository is anything implementing an I…Repository port.
        "Repository" => type.GetInterfaces().Any(static i => i.Name.EndsWith("Repository", StringComparison.Ordinal)),
        // FluentValidation validators derive from AbstractValidator<T>.
        "Validator" => InheritsFromOpenGeneric(type, "AbstractValidator`1"),
        _ => false,
    };

    private static bool InheritsFromOpenGeneric(Type type, string baseName)
    {
        for (Type? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.Name, baseName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAwaitable(Type returnType)
    {
        if (returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            return true;
        }

        return returnType.IsGenericType
            && (returnType.GetGenericTypeDefinition() == typeof(Task<>)
                || returnType.GetGenericTypeDefinition() == typeof(ValueTask<>));
    }

    private static bool IsInterfaceImplementation(MethodInfo method)
    {
        Type declaring = method.DeclaringType!;

        return declaring.GetInterfaces().Any(contract =>
            declaring.GetInterfaceMap(contract).TargetMethods.Contains(method));
    }

    private static bool IsCompilerGenerated(Type type) =>
        type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false)
        || type.Name.Contains('<', StringComparison.Ordinal);
}
