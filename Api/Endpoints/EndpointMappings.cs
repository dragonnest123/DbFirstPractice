using System.Text;
using Api.Services;
using Shared.Services;
using Shared.Utils;

namespace Api.Endpoints;

public static class EndpointMappings
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", (ActionCatalogService actionCatalog) =>
            actionCatalog.IsReady()
                ? Results.Ok(new { status = "ready" })
                : Results.Json(
                    new { status = "not_ready", code = "dependency.unavailable" },
                    statusCode: 503));

        app.MapGet("/metrics", (WorkflowMetricsService metrics) =>
        {
            try
            {
                var samples = metrics.Collect().ToList();
                samples.AddRange(metrics.ProcessOnly(ProcessStart));
                return Results.Text(
                    OpenMetricsWriter.Render(samples),
                    OpenMetricsWriter.ContentType,
                    Encoding.UTF8);
            }
            catch
            {
                return Results.Text(
                    OpenMetricsWriter.Render(metrics.ProcessOnly(ProcessStart)),
                    OpenMetricsWriter.ContentType,
                    Encoding.UTF8);
            }
        });

        app.MapGet("/openapi/default.json", OpenApiDefaultEndpoint.Handle);
        app.MapGet("/openapi/actions/{module}/{action}/{version:int}.json", OpenApiActionEndpoint.Handle);

        app.MapPost("/api/{module}/{action}", ActionEndpoint.Handle);
    }

    private static readonly DateTimeOffset ProcessStart = DateTimeOffset.UtcNow;
}
