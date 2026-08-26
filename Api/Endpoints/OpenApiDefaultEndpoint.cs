using System.Text.Json.Nodes;
using Api.Services;
using Npgsql;

namespace Api.Endpoints;

public static class OpenApiDefaultEndpoint
{
    public static async Task<IResult> Handle(CatalogService catalog)
    {
        try
        {
            await using var conn = new NpgsqlConnection(catalog.ConnectionString);
            await conn.OpenAsync();

            await using var cmd =
                new NpgsqlCommand(
                    "SELECT module, action, request_schema, response_schema FROM api.action_catalog WHERE enabled AND is_default",
                    conn);

            var paths = new JsonObject();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var module = r.GetString(0);
                var action = r.GetString(1);
                var req = JsonNode.Parse(r.GetString(2));
                var resp = JsonNode.Parse(r.GetString(3));
                var path = $"/api/{module}/{action}";
                paths[path] = new JsonObject
                {
                    ["post"] = new JsonObject
                    {
                        ["requestBody"] = new JsonObject
                        {
                            ["content"] = new JsonObject
                                { ["application/json"] = new JsonObject { ["schema"] = req } }
                        },
                        ["responses"] = new JsonObject
                        {
                            ["200"] = new JsonObject
                            {
                                ["content"] = new JsonObject
                                    { ["application/json"] = new JsonObject { ["schema"] = resp } }
                            }
                        }
                    }
                };
            }

            var doc = new JsonObject
            {
                ["openapi"] = "3.0.0", 
                ["info"] = new JsonObject { ["title"] = "course", ["version"] = "1.0" },
                ["paths"] = paths
            };
            return Results.Text(
                doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }),
                "application/json");
        }
        catch
        {
            return Results.Json(new { status = "error" }, statusCode: 503);
        }
    }
}