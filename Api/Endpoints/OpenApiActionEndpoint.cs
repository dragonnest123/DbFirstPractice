using System.Text.Json.Nodes;
using Npgsql;
using Shared.Services;

namespace Api.Endpoints;

public static class OpenApiActionEndpoint
{
    public static async Task<IResult> Handle(string module, string action, int version, ActionCatalogService actionCatalog)
    {
        try
        {
            await using var conn = new NpgsqlConnection(actionCatalog.ConnectionString);
            await conn.OpenAsync();

            await using var cmd =
                new NpgsqlCommand(
                    "SELECT request_schema, response_schema FROM api.action_catalog WHERE module=@m AND action=@a AND version=@v",
                    conn);
            cmd.Parameters.AddWithValue("m", module);
            cmd.Parameters.AddWithValue("a", action);
            cmd.Parameters.AddWithValue("v", version);

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return Results.NotFound();

            var req = JsonNode.Parse(r.GetString(0));
            var resp = JsonNode.Parse(r.GetString(1));
            var path = $"/api/{module}/{action}";
            var doc = new JsonObject
            {
                ["openapi"] = "3.0.0",
                ["info"] = new JsonObject
                    { ["title"] = $"{module}.{action} v{version}", ["version"] = version.ToString() },
                ["paths"] = new JsonObject
                {
                    [path] = new JsonObject
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
                    }
                }
            };
            return Results.Text(
                doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }),
                "application/json");
        }
        catch
        {
            return Results.Json(new { status = "error" }, statusCode: 500);
        }
    }
}