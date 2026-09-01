using Api.Endpoints;
using Api.Services;
using Shared.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton(sp => new ActionCatalogService(ResolveConnectionString(sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton<DispatchService>();
builder.Services.AddSingleton<IdempotencyService>();
builder.Services.AddSingleton<ActionInvoker>();

var app = builder.Build();

EndpointMappings.Map(app);

app.Run();
return;

string ResolveConnectionString(IConfiguration cfg) =>
    cfg.GetConnectionString("CourseDb")
    ?? cfg["POSTGRES_CONNECTION"]
    ?? cfg["ConnectionStrings__CourseDb"]
    ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
    ?? "Host=postgres;Port=5432;Database=course;Username=course_runtime;Password=runtime;Include Error Detail=false";