using Xunit;
using ECommerce.Inventory.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace InventoryService.Tests;

/// CreateInventoryItem over a real gRPC channel: the wire format, the status codes Laravel will
/// see, and the shared secret that guards the call. The two hosts differ only in whether a
/// Provisioning:Token is configured.
[Collection(nameof(SqlServerCollection))]
public class ProvisioningGrpcTests(SqlServerDatabaseFixture fixture) : IAsyncLifetime
{
    private const string Token = "test-only-provisioning-token";

    private WebApplicationFactory<Program>? _configured;
    private WebApplicationFactory<Program>? _unconfigured;
    private GrpcChannel? _configuredChannel;
    private GrpcChannel? _unconfiguredChannel;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Inventory.InventoryClient Configured => new(_configuredChannel!);

    private Inventory.InventoryClient Unconfigured => new(_unconfiguredChannel!);

    private static Metadata WithToken(string token) => new() { { "x-provisioning-token", token } };

    public ValueTask InitializeAsync()
    {
        if (fixture.SkipReason is not null)
        {
            return ValueTask.CompletedTask;
        }

        _configured = CreateFactory(token: Token);
        _unconfigured = CreateFactory(token: string.Empty);
        _configuredChannel = CreateChannel(_configured);
        _unconfiguredChannel = CreateChannel(_unconfigured);

        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> CreateFactory(string token) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);   // keeps the development seeder out
            builder.UseSetting("ConnectionStrings:InventoryDb", fixture.ConnectionString);
            builder.UseSetting("Provisioning:Token", token);
        });

    private static GrpcChannel CreateChannel(WebApplicationFactory<Program> factory) =>
        GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler()
        });

    public async ValueTask DisposeAsync()
    {
        _configuredChannel?.Dispose();
        _unconfiguredChannel?.Dispose();

        if (_configured is not null)
        {
            await _configured.DisposeAsync();
        }

        if (_unconfigured is not null)
        {
            await _unconfigured.DisposeAsync();
        }
    }

    private async Task ArrangeAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.ResetAsync();
    }

    private static CreateInventoryItemRequest Request(long productId, int quantity) =>
        new() { ProductId = productId, InitialQuantity = quantity };

    [Fact]
    public async Task A_valid_token_creates_the_item_and_GetStock_sees_it()
    {
        await ArrangeAsync();

        var created = await Configured.CreateInventoryItemAsync(
            Request(801, 20), WithToken(Token), cancellationToken: Ct).ResponseAsync;
        var stock = await Configured.GetStockAsync(
            new GetStockRequest { ProductId = 801 }, cancellationToken: Ct).ResponseAsync;

        Assert.Equal(CreateInventoryItemStatus.Created, created.Status);
        Assert.Equal(801, created.ProductId);
        Assert.Equal(20, stock.AvailableQuantity);
    }

    [Fact]
    public async Task Repeating_the_request_reports_ALREADY_CREATED_and_adds_nothing()
    {
        await ArrangeAsync();
        await Configured.CreateInventoryItemAsync(Request(802, 20), WithToken(Token), cancellationToken: Ct).ResponseAsync;

        var replay = await Configured.CreateInventoryItemAsync(
            Request(802, 20), WithToken(Token), cancellationToken: Ct).ResponseAsync;

        Assert.Equal(CreateInventoryItemStatus.AlreadyCreated, replay.Status);
        Assert.Equal((20, 0), await fixture.ReadQuantitiesAsync(802));
    }

    [Fact]
    public async Task A_different_quantity_for_an_existing_product_maps_to_AlreadyExists()
    {
        await ArrangeAsync();
        await Configured.CreateInventoryItemAsync(Request(803, 20), WithToken(Token), cancellationToken: Ct).ResponseAsync;

        var ex = await Assert.ThrowsAsync<RpcException>(() => Configured.CreateInventoryItemAsync(
            Request(803, 5), WithToken(Token), cancellationToken: Ct).ResponseAsync);

        Assert.Equal(StatusCode.AlreadyExists, ex.StatusCode);
        Assert.Equal((20, 0), await fixture.ReadQuantitiesAsync(803));
    }

    [Fact]
    public async Task Invalid_arguments_map_to_InvalidArgument()
    {
        await ArrangeAsync();

        var negative = await Assert.ThrowsAsync<RpcException>(() => Configured.CreateInventoryItemAsync(
            Request(804, -1), WithToken(Token), cancellationToken: Ct).ResponseAsync);
        var badId = await Assert.ThrowsAsync<RpcException>(() => Configured.CreateInventoryItemAsync(
            Request(0, 5), WithToken(Token), cancellationToken: Ct).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, negative.StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, badId.StatusCode);
        Assert.Equal(0, await fixture.CountInventoryRowsAsync(804));
    }

    [Fact]
    public async Task A_missing_token_is_refused_and_nothing_is_created()
    {
        await ArrangeAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() => Configured.CreateInventoryItemAsync(
            Request(805, 20), cancellationToken: Ct).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.Equal(0, await fixture.CountInventoryRowsAsync(805));
    }

    [Fact]
    public async Task A_wrong_token_is_refused_and_nothing_is_created()
    {
        await ArrangeAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(() => Configured.CreateInventoryItemAsync(
            Request(806, 20), WithToken("not-the-token"), cancellationToken: Ct).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.Equal(0, await fixture.CountInventoryRowsAsync(806));
    }

    [Fact]
    public async Task The_token_is_checked_before_the_request_is_validated()
    {
        await ArrangeAsync();

        // An invalid request from an unauthenticated caller must not reveal anything about
        // validation: it is refused for the token first.
        var ex = await Assert.ThrowsAsync<RpcException>(() => Configured.CreateInventoryItemAsync(
            Request(0, -1), cancellationToken: Ct).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task A_server_with_no_token_configured_refuses_every_call_even_with_a_header()
    {
        await ArrangeAsync();

        var withHeader = await Assert.ThrowsAsync<RpcException>(() => Unconfigured.CreateInventoryItemAsync(
            Request(807, 20), WithToken(Token), cancellationToken: Ct).ResponseAsync);
        var withEmptyHeader = await Assert.ThrowsAsync<RpcException>(() => Unconfigured.CreateInventoryItemAsync(
            Request(807, 20), WithToken(string.Empty), cancellationToken: Ct).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, withHeader.StatusCode);
        Assert.Equal(StatusCode.FailedPrecondition, withEmptyHeader.StatusCode);
        Assert.Equal(0, await fixture.CountInventoryRowsAsync(807));
    }

    [Fact]
    public async Task The_other_operations_still_work_without_any_token()
    {
        await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 808, onHand: 9);

        var stock = await Unconfigured.GetStockAsync(
            new GetStockRequest { ProductId = 808 }, cancellationToken: Ct).ResponseAsync;

        Assert.Equal(9, stock.AvailableQuantity);
    }
}
