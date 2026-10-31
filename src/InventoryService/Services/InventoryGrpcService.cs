using ECommerce.Inventory.V1;
using Grpc.Core;

namespace InventoryService.Services;

public class InventoryGrpcService(ILogger<InventoryGrpcService> logger) : Inventory.InventoryBase
{
    public override Task<GetStockResponse> GetStock(GetStockRequest request, ServerCallContext context)
    {
        logger.LogWarning("GetStock called for product {ProductId}, but the service is not implemented yet", request.ProductId);

        throw new RpcException(new Status(
            StatusCode.Unimplemented,
            "GetStock is not implemented yet."));
    }

    public override Task<ReserveStockResponse> ReserveStock(ReserveStockRequest request, ServerCallContext context)
    {
        logger.LogWarning(
            "ReserveStock called for reservation {ReservationId} with {ItemCount} item(s), but the service is not implemented yet",
            request.ReservationId,
            request.Items.Count);

        throw new RpcException(new Status(
            StatusCode.Unimplemented,
            "ReserveStock is not implemented yet. No quantities were reserved."));
    }

    public override Task<ReleaseStockResponse> ReleaseStock(ReleaseStockRequest request, ServerCallContext context)
    {
        logger.LogWarning("ReleaseStock called for reservation {ReservationId}, but the service is not implemented yet", request.ReservationId);

        throw new RpcException(new Status(
            StatusCode.Unimplemented,
            "ReleaseStock is not implemented yet. No quantities were restored."));
    }
}
