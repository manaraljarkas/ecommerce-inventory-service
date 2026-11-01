using Xunit;
using ECommerce.Inventory.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace InventoryService.Tests;

/// Exercises the published contract over a real gRPC channel: generated client stubs, protobuf
/// serialization, the interceptor and the status codes a Laravel client would actually see.

/// The operation tests cover behaviour; these cover the wire. They are what would catch a
/// business outcome being returned with the wrong status code, which no in-process test of
/// <c>InventoryOperations</c> can detect.
[Collection(nameof(SqlServerCollection))]
public class GrpcContractTests(SqlServerDatabaseFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private GrpcChannel? _channel;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Inventory.InventoryClient Client => new(_channel!);

    // Thin wrappers so every call carries the test's cancellation token and returns the
    // response task directly, which keeps the assertions readable.
    private Task<GetStockResponse> GetStock(GetStockRequest request) =>
        Client.GetStockAsync(request, cancellationToken: Ct).ResponseAsync;

    private Task<ReserveStockResponse> Reserve(ReserveStockRequest request) =>
        Client.ReserveStockAsync(request, cancellationToken: Ct).ResponseAsync;

    private Task<ReleaseStockResponse> Release(string reservationId) =>
        Client.ReleaseStockAsync(new ReleaseStockRequest { ReservationId = reservationId },
            cancellationToken: Ct).ResponseAsync;

    public ValueTask InitializeAsync()
    {
        if (fixture.SkipReason is not null)
        {
            return ValueTask.CompletedTask;
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);   // keeps the development seeder out
            builder.UseSetting("ConnectionStrings:InventoryDb", fixture.ConnectionString);
        });

        _channel = GrpcChannel.ForAddress(_factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = _factory.Server.CreateHandler()
        });

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _channel?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    private async Task ArrangeAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.ResetAsync();
    }

    private static ReserveStockRequest Request(string reservationId, params (long ProductId, int Quantity)[] items)
    {
        var request = new ReserveStockRequest { ReservationId = reservationId };
        request.Items.AddRange(items.Select(i => new ReserveStockItem
        {
            ProductId = i.ProductId,
            Quantity = i.Quantity
        }));
        return request;
    }

    [Fact]
    public async Task GetStock_returns_available_quantity_over_the_wire()
    {
        await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 77, onHand: 9, reserved: 4);

        var response = await GetStock(new GetStockRequest { ProductId = 77 });

        Assert.Equal(77, response.ProductId);
        Assert.Equal(5, response.AvailableQuantity);
    }

    [Fact]
    public async Task GetStock_unknown_product_maps_to_NotFound()
    {
        await ArrangeAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() => GetStock(new GetStockRequest { ProductId = 12345 }));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task GetStock_invalid_product_id_maps_to_InvalidArgument()
    {
        await ArrangeAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() => GetStock(new GetStockRequest { ProductId = 0 }));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task ReserveStock_success_and_replay_are_normal_responses()
    {
        await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 5, onHand: 10);

        var first = await Reserve(Request("wire-ok", (5, 2)));
        var second = await Reserve(Request("wire-ok", (5, 2)));

        Assert.Equal(ReserveStockStatus.Reserved, first.Status);
        Assert.Equal("wire-ok", first.ReservationId);
        Assert.Equal(ReserveStockStatus.AlreadyReserved, second.Status);
        Assert.Equal(2, (await fixture.ReadQuantitiesAsync(5)).Reserved);
    }

    [Fact]
    public async Task ReserveStock_insufficient_stock_is_a_response_not_an_error()
    {
        await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 6, onHand: 1);

        // The contract's central distinction: a business rejection arrives as OK with a status
        // in the body, so Laravel can tell it apart from an infrastructure failure.
        var response = await Reserve(Request("wire-short", (6, 4)));

        Assert.Equal(ReserveStockStatus.InsufficientStock, response.Status);
        var detail = Assert.Single(response.InsufficientItems);
        Assert.Equal(6, detail.ProductId);
        Assert.Equal(4, detail.RequestedQuantity);
        Assert.Equal(1, detail.AvailableQuantity);
    }

    [Fact]
    public async Task ReserveStock_reused_id_with_different_items_maps_to_AlreadyExists()
    {
        await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 7, onHand: 10);

        await Reserve(Request("wire-conflict", (7, 1)));

        var ex = await Assert.ThrowsAsync<RpcException>(() => Reserve(Request("wire-conflict", (7, 2))));

        Assert.Equal(StatusCode.AlreadyExists, ex.StatusCode);
    }

    [Fact]
    public async Task ReserveStock_after_release_maps_to_FailedPrecondition()
    {
        await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 8, onHand: 10);

        await Reserve(Request("wire-spent", (8, 1)));
        await Release("wire-spent");

        var ex = await Assert.ThrowsAsync<RpcException>(() => Reserve(Request("wire-spent", (8, 1))));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task ReserveStock_unknown_product_maps_to_NotFound()
    {
        await ArrangeAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() => Reserve(Request("wire-missing", (4242, 1))));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task ReserveStock_malformed_request_maps_to_InvalidArgument()
    {
        await ArrangeAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            Reserve(new ReserveStockRequest { ReservationId = "" }));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task ReleaseStock_release_then_repeat_then_unknown()
    {
        await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 9, onHand: 10);

        await Reserve(Request("wire-rel", (9, 3)));

        var released = await Release("wire-rel");
        var repeated = await Release("wire-rel");

        Assert.Equal(ReleaseStockStatus.Released, released.Status);
        Assert.Equal(ReleaseStockStatus.AlreadyReleased, repeated.Status);
        Assert.Equal(0, (await fixture.ReadQuantitiesAsync(9)).Reserved);

        var ex = await Assert.ThrowsAsync<RpcException>(() => Release("wire-nothing"));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }
}
