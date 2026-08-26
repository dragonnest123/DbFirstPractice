using Api.Endpoints;
using Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<DispatchService>();
builder.Services.AddSingleton<IdempotencyService>();
builder.Services.AddSingleton<ActionInvoker>();

var app = builder.Build();

EndpointMappings.Map(app);

app.Run();