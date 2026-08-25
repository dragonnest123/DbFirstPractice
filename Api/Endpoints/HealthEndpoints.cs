using Api.Services;

namespace Api.Endpoints;

public static class HealthEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/health/ready", (CatalogService catalog) => catalog.IsReady()
            ? Results.Ok(new { status = "ok" })
            : Results.Json(new { status = "error" }, statusCode: 503));
    }
}
