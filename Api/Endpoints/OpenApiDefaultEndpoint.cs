using System.Text.Json.Nodes;
using Npgsql;
using Shared.Services;

namespace Api.Endpoints;

public static class OpenApiDefaultEndpoint
{
    public static async Task<IResult> Handle(ActionCatalogService actionCatalog)
    {
        try
        {
            await using var conn = new NpgsqlConnection(actionCatalog.ConnectionString);
            await conn.OpenAsync();

            await using var cmd =
                new NpgsqlCommand(
                    "SELECT module, action, request_schema, response_schema, idempotency_mode FROM api.action_catalog WHERE enabled AND is_default",
                    conn);

            var paths = new JsonObject();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var module = r.GetString(0);
                var action = r.GetString(1);
                var req = JsonNode.Parse(r.GetString(2));
                var resp = JsonNode.Parse(r.GetString(3));
                var idempotencyRequired = r.GetString(4) == "required";
                var path = $"/api/{module}/{action}";
                paths[path] = new JsonObject
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
                };
            }

            var doc = new JsonObject
            {
                ["openapi"] = "3.1.0",
                ["info"] = new JsonObject { ["title"] = "course", ["version"] = "1.0" },
                ["jsonSchemaDialect"] = "https://json-schema.org/draft/2020-12/schema",
                ["paths"] = paths,
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
            return Results.Json(new { status = "error" }, statusCode: 503);
        }
    }
}