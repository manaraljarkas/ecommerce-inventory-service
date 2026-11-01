using InventoryService.Application;
using InventoryService.Persistence;
using InventoryService.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddGrpc(options =>
{
    // One place to turn escaped exceptions into status codes, so the service methods stay free
    // of try/catch and no server internals reach the client.
    options.Interceptors.Add<ExceptionInterceptor>();
});

// The connection string is machine-specific and never committed. Supply it through user
// secrets ("ConnectionStrings:InventoryDb") or the ConnectionStrings__InventoryDb
// environment variable. Failing here, at startup, is deliberate: a silent fallback would
// only surface later as a confusing query error.
var connectionString = builder.Configuration.GetConnectionString("InventoryDb");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'InventoryDb' is not configured. Set it with " +
        "'dotnet user-secrets set \"ConnectionStrings:InventoryDb\" \"<connection string>\"' " +
        "or the ConnectionStrings__InventoryDb environment variable.");
}

builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddScoped<InventoryOperations>();
builder.Services.Configure<InventorySeedOptions>(
    builder.Configuration.GetSection(InventorySeedOptions.SectionName));

var app = builder.Build();

// Development-only stock seeding, off unless InventorySeed:Enabled is true. It inserts rows for
// products that have none and never touches quantities that already exist. Schema changes stay
// with EF Core migrations: nothing here creates or migrates a database.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await DevelopmentSeeder.SeedAsync(
        scope.ServiceProvider.GetRequiredService<InventoryDbContext>(),
        scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<InventorySeedOptions>>().Value,
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DevelopmentSeeder)));
}

// Configure the HTTP request pipeline.
app.MapGrpcService<InventoryGrpcService>();
app.MapGet("/", () => "Inventory gRPC service. Communication with gRPC endpoints must be made through a gRPC client using the ecommerce.inventory.v1.Inventory contract.");

app.Run();

///Exposed so the integration tests can host this application with WebApplicationFactory.
public partial class Program;
