using System.Reflection;
using Trellis.Api.Contracts.Invoices;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Results;

namespace Trellis.ArchitectureTests;

/// <summary>Assembly anchors and namespace roots for the architecture fitness tests.</summary>
internal static class Architecture
{
    public const string SharedKernel = "Trellis.SharedKernel";
    public const string Domain = "Trellis.Domain";
    public const string Application = "Trellis.Application";
    public const string Infrastructure = "Trellis.Infrastructure";
    public const string Persistence = "Trellis.Persistence";
    public const string Presentation = "Trellis.Api";   // covers Api, Api.Common, Api.Contracts
    public const string Ui = "Trellis.UI";

    public const string EntityFramework = "Microsoft.EntityFrameworkCore";
    public const string AspNetCore = "Microsoft.AspNetCore";

    public static Assembly SharedKernelAssembly => typeof(Result).Assembly;
    public static Assembly DomainAssembly => typeof(Invoice).Assembly;
    public static Assembly ApplicationAssembly => typeof(Application.DependencyInjection).Assembly;
    public static Assembly InfrastructureAssembly => typeof(Infrastructure.DependencyInjection).Assembly;
    public static Assembly PersistenceAssembly => typeof(Persistence.DependencyInjection).Assembly;
    public static Assembly ApiContractsAssembly => typeof(InvoiceDto).Assembly;
    public static Assembly ApiCommonAssembly => typeof(Api.Common.DependencyInjection).Assembly;
    public static Assembly ApiAssembly => typeof(Program).Assembly;
#if (IncludeUI)
    public static Assembly UiCommonAssembly => typeof(Trellis.UI.Common.DependencyInjection).Assembly;
#endif
}
