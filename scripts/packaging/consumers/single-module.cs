using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Validation;
using PouryaKit.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.UsePostgres<NotesDbContext>(connectionName: "app", schema: "notes");
builder.Services.AddPouryaKitCqrs(typeof(NotesDbContext).Assembly);
builder.Services.AddPouryaKitValidation(typeof(NotesDbContext).Assembly);
builder.Services.AddPouryaKitDapper("app", typeof(NotesDbContext).Assembly);
builder.Services.AddPouryaKitWeb();

using var app = builder.Build();
await using var scope = app.Services.CreateAsyncScope();
scope.ServiceProvider.BeginOperation(new OperationContext(Actor.System("package-consumer")));
_ = scope.ServiceProvider.GetRequiredService<IDispatcher>();

internal sealed class NotesDbContext(DbContextOptions<NotesDbContext> options) : PouryaKitDbContext(options);
