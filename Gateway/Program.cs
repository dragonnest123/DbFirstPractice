using Gateway;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);

builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["InternalApi:BaseUrl"] ?? "http://api:8080");
    client.Timeout = TimeSpan.FromSeconds(75);
});

var app = builder.Build();

app.Use(GatewayRoutingMiddleware.Create());

app.Run();
