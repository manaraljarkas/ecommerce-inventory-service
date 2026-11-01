using ECommerce.Inventory.V1;
using Grpc.Core;
using InventoryService.Application;

namespace InventoryService.Services;


/// gRPC transport for the contract in Protos/inventory.proto.

/// This layer only translates: protobuf in, application call, outcome mapped to a response or a
/// status code. The business rules and all database work live in InventoryOperations.
///
/// The split between a response body and a status code follows the contract: insufficient stock
/// is a normal answer carried in ReserveStockResponse, because Laravel has to show the customer
/// which lines failed. Everything else - bad input, unknown entities, a spent identifier,
/// infrastructure trouble - is a gRPC status, so the caller can tell a business rejection from a
/// service problem without parsing messages.

public class InventoryGrpcService(
    InventoryOperations operations,
    ILogger<InventoryGrpcService> logger) : Inventory.InventoryBase
{
    public override async Task<GetStockResponse> GetStock(
        GetStockRequest request, ServerCallContext context)
    {
        var result = await operations.GetStockAsync(request.ProductId, context.CancellationToken);

        return result.Outcome switch
        {
            GetStockOutcome.Success => new GetStockResponse
            {
                ProductId = result.ProductId,
                AvailableQuantity = result.AvailableQuantity
            },

            GetStockOutcome.InvalidArgument =>
                throw new RpcException(new Status(StatusCode.InvalidArgument, result.ValidationError!)),

            GetStockOutcome.ProductNotFound =>
                throw new RpcException(new Status(StatusCode.NotFound,
                    $"No inventory record exists for product {request.ProductId}.")),

            _ => throw UnexpectedOutcome(result.Outcome)
        };
    }

    public override async Task<ReserveStockResponse> ReserveStock(
        ReserveStockRequest request, ServerCallContext context)
    {
        var items = request.Items
            .Select(i => new RequestedItem(i.ProductId, i.Quantity))
            .ToList();

        var result = await operations.ReserveStockAsync(
            request.ReservationId, items, context.CancellationToken);

        switch (result.Outcome)
        {
            case ReserveStockOutcome.Reserved:
                return new ReserveStockResponse
                {
                    Status = ReserveStockStatus.Reserved,
                    ReservationId = request.ReservationId
                };

            case ReserveStockOutcome.AlreadyReserved:
                return new ReserveStockResponse
                {
                    Status = ReserveStockStatus.AlreadyReserved,
                    ReservationId = request.ReservationId
                };

            case ReserveStockOutcome.InsufficientStock:
                var response = new ReserveStockResponse
                {
                    Status = ReserveStockStatus.InsufficientStock,
                    ReservationId = request.ReservationId
                };
                response.InsufficientItems.AddRange(result.Shortfalls.Select(s => new InsufficientStockItem
                {
                    ProductId = s.ProductId,
                    RequestedQuantity = s.RequestedQuantity,
                    AvailableQuantity = s.AvailableQuantity
                }));
                return response;

            case ReserveStockOutcome.IdReusedWithDifferentItems:
                throw new RpcException(new Status(StatusCode.AlreadyExists,
                    $"Reservation '{request.ReservationId}' is already active with different items."));

            case ReserveStockOutcome.IdAlreadyReleased:
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    $"Reservation '{request.ReservationId}' has already been released and cannot be reserved again."));

            case ReserveStockOutcome.ProductNotFound:
                throw new RpcException(new Status(StatusCode.NotFound,
                    $"No inventory record exists for product(s): {string.Join(", ", result.MissingProductIds)}."));

            case ReserveStockOutcome.InvalidArgument:
                throw new RpcException(new Status(StatusCode.InvalidArgument, result.ValidationError!));

            default:
                throw UnexpectedOutcome(result.Outcome);
        }
    }

    public override async Task<ReleaseStockResponse> ReleaseStock(
        ReleaseStockRequest request, ServerCallContext context)
    {
        var result = await operations.ReleaseStockAsync(request.ReservationId, context.CancellationToken);

        return result.Outcome switch
        {
            ReleaseStockOutcome.Released => new ReleaseStockResponse
            {
                Status = ReleaseStockStatus.Released,
                ReservationId = request.ReservationId
            },

            ReleaseStockOutcome.AlreadyReleased => new ReleaseStockResponse
            {
                Status = ReleaseStockStatus.AlreadyReleased,
                ReservationId = request.ReservationId
            },

            // Point in time only: this says no reservation existed as the call was processed.
            // It does not resolve an earlier ReserveStock for the same id that may still be in
            // flight - see the caller responsibilities in Protos/inventory.proto.
            ReleaseStockOutcome.NotFound =>
                throw new RpcException(new Status(StatusCode.NotFound,
                    $"No reservation exists for id '{request.ReservationId}'.")),

            ReleaseStockOutcome.InvalidArgument =>
                throw new RpcException(new Status(StatusCode.InvalidArgument, result.ValidationError!)),

            _ => throw UnexpectedOutcome(result.Outcome)
        };
    }

    private RpcException UnexpectedOutcome<T>(T outcome) where T : struct, Enum
    {
        logger.LogError("Unhandled {OutcomeType} value {Outcome}", typeof(T).Name, outcome);
        return new RpcException(new Status(StatusCode.Internal, "An unexpected error occurred."));
    }
}
