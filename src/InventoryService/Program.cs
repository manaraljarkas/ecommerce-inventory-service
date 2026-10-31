using InventoryService.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddGrpc();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapGrpcService<InventoryGrpcService>();
app.MapGet("/", () => "Inventory gRPC service. Communication with gRPC endpoints must be made through a gRPC client using the ecommerce.inventory.v1.Inventory contract.");

app.Run();
