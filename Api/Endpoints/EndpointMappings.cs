using Api.Services;

namespace Api.Endpoints;

public static class EndpointMappings
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/health/ready", (CatalogService catalog) 
            => catalog.IsReady() ? Results.Ok(new { status = "ok" }) : Results.Json(new { status = "error" }, statusCode: 503));
        
        app.MapGet("/openapi/default.json", OpenApiDefaultEndpoint.Handle);
        app.MapGet("/openapi/actions/{module}/{action}/{version:int}.json", OpenApiActionEndpoint.Handle);
        
        app.MapPost("/api/{module}/{action}", ActionEndpoint.Handle);
    }
}