using System.Reflection;
using System.Text.Json;
using Atm.Api.Infrastructure;
using Atm.Business;
using Atm.Business.Common;
using Atm.DataAccess;
using Atm.DataAccess.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(o => AtmJson.Configure(o.JsonSerializerOptions));

builder.Services.Configure<ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = ProblemDetailsFactoryExtensions.InvalidModelState);
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    ctx.ProblemDetails.Extensions.TryAdd("traceId", ctx.HttpContext.TraceIdentifier);
    if (!ctx.ProblemDetails.Extensions.ContainsKey("errorCode"))
    {
        ctx.ProblemDetails.Extensions["errorCode"] = ctx.ProblemDetails.Status switch
        {
            404 => "NOT_FOUND",
            405 => "METHOD_NOT_ALLOWED",
            415 => "UNSUPPORTED_MEDIA_TYPE",
            _ => "HTTP_" + ctx.ProblemDetails.Status
        };
    }
});
builder.Services.AddExceptionHandler<AtmExceptionHandler>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ATM Cash Management API",
        Version = "v1",
        Description = "Cash management backend for a bank's ATM network: cassette configuration, cash loading, " +
                      "two-step withdrawals, deposits, reconciliation with four-eyes approval, ledger and reporting."
    });
    var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml))
    {
        o.IncludeXmlComments(xml);
    }
});

builder.Services.AddHealthChecks().AddDbContextCheck<AtmDbContext>("database");

builder.Services.AddBusiness(builder.Configuration);
builder.Services.AddDataAccess(builder.Configuration);

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await app.Services.MigrateDatabaseAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseSwagger();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "ATM Cash Management API v1");
    o.DocumentTitle = "ATM Cash Management API";
});

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), e.Value.Description }),
            duration = report.TotalDuration
        }));
    }
});
app.MapControllers();

app.Run();

public partial class Program;
