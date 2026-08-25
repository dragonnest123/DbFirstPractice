using Api.Endpoints;
using Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton<JwtValidator>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<DispatchService>();

var app = builder.Build();

HealthEndpoints.Map(app);
OpenApiEndpoints.Map(app);
ActionEndpoints.Map(app);

app.Run();