using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;
using PouryaKit.Reporting;
using PouryaKit.Reporting.Module;

// The packed report Module as a Host composes it: added once with its Modules, its settings in the Host's configuration,
// and the two ports the Host implements for it. Composed, never built or run.
var builder = WebApplication.CreateBuilder();
builder.AddPouryaKitModules(modules => modules.Add<ReportModule>());
builder.Configuration["PouryaKit:Reports:Retention"] = "30.00:00:00";
builder.Services.AddSingleton<IReportRequestAuthorizer, Inventory>();
builder.Services.AddSingleton<IReportLayouts, Inventory>();
Console.WriteLine(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IReportRenderer)) ? 1 : 0);

sealed class Inventory : IReportRequestAuthorizer, IReportLayouts
{
    public Task<bool> MayRequestAsync(OperationContext context, ReportRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(request.Kind == "stock" && context.Tenant is not null);

    public Task<Result<ReportLayout>> LayOutAsync(OperationContext context, ReportRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Result<ReportLayout>>(new ReportLayout("Stock", new ReportTable([new("Item"), new("On hand", ReportAlignment.Right)], [["Tea", "12"]])));
}
