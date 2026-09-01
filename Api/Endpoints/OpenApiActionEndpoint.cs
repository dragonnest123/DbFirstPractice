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
                    "SELECT request_schema, response_schema, idempotency_mode FROM api.action_catalog WHERE module=@m AND action=@a AND version=@v",
                    conn);
            cmd.Parameters.AddWithValue("m", module);
            cmd.Parameters.AddWithValue("a", action);
            cmd.Parameters.AddWithValue("v", version);

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return Results.NotFound();

            var req = JsonNode.Parse(r.GetString(0));
            var resp = JsonNode.Parse(r.GetString(1));
            var idempotencyRequired = r.GetString(2) == "required";
            var path = $"/api/{module}/{action}";

            var doc = new JsonObject
            {
                ["openapi"] = "3.1.0",
                ["info"] = new JsonObject
                    { ["title"] = $"{module}.{action} v{version}", ["version"] = version.ToString() },
                ["jsonSchemaDialect"] = "https://json-schema.org/draft/2020-12/schema",
                ["paths"] = new JsonObject
                {
                    [path] = new JsonObject
                    {
                        ["post"] = new JsonObject
                        {
                            ["requestBody"] = new JsonObject
                            {
                                ["content"] = new JsonObject
                                {
                                    ["application/json"] = new JsonObject { ["schema"] = req }
                                }
                            },
                            ["parameters"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["name"] = "X-Action-Version",
                                    ["in"] = "header",
                                    ["required"] = false,
                                    ["description"] = "explicit action version; default version is used when absent",
                                    ["schema"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 }
                                },
                                new JsonObject
                                {
                                    ["name"] = "Idempotency-Key",
                                    ["in"] = "header",
                                    ["required"] = idempotencyRequired,
                                    ["description"] = "idempotency key; required when manifest idempotency_mode is required",
                                    ["schema"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128 }
                                }
                            },
                            ["responses"] = new JsonObject
                            {
                                ["200"] = new JsonObject
                                {
                                    ["description"] = "success",
                                    ["content"] = new JsonObject
                                    {
                                        ["application/json"] = new JsonObject { ["schema"] = resp }
                                    }
                                }
                            }
                        }
                    }
                },
                ["components"] = new JsonObject
                {
                    ["securitySchemes"] = new JsonObject
                    {
                        ["bearerAuth"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" }
                    }
                },
                ["security"] = new JsonArray { new JsonObject { ["bearerAuth"] = new JsonArray() } }
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