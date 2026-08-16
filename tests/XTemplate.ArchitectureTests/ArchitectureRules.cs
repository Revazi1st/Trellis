using System.Reflection;
using XTemplate.Api.Contracts.Invoices;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Results;

namespace XTemplate.ArchitectureTests;

/// <summary>Assembly anchors and namespace roots for the architecture fitness tests.</summary>
internal static class Architecture
{
    public const string SharedKernel = "XTemplate.SharedKernel";
    public const string Domain = "XTemplate.Domain";
    public const string Application = "XTemplate.Application";
    public const string Infrastructure = "XTemplate.Infrastructure";
    public const string Persistence = "XTemplate.Persistence";
    public const string Presentation = "XTemplate.Api";   // covers Api, Api.Common, Api.Contracts
    public const string Ui = "XTemplate.UI";

    public const string EntityFramework = "Microsoft.EntityFrameworkCore";
    public const string AspNetCore = "Microsoft.AspNetCore";

    public static Assembly SharedKernelAssembly => typeof(Result).Assembly;
    public static Assembly DomainAssembly => typeof(Invoice).Assembly;
    public static Assembly ApplicationAssembly => typeof(Application.DependencyInjection).Assembly;
    public static Assembly InfrastructureAssembly => typeof(Infrastructure.DependencyInjection).Assembly;
    public static Assembly PersistenceAssembly => typeof(Persistence.DependencyInjection).Assembly;
    public static Assembly ApiContractsAssembly => typeof(InvoiceDto).Assembly;
    public static Assembly ApiAssembly => typeof(Program).Assembly;
#if (IncludeUI)
    public static Assembly UiCommonAssembly => typeof(XTemplate.UI.Common.DependencyInjection).Assembly;
#endif
}
