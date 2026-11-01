using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Data.SqlClient;

namespace InventoryService.Services;


/// Turns anything that escapes a gRPC method into an appropriate status code, and keeps server
/// internals out of the response.

/// Without this, an unhandled exception reaches the client as an INTERNAL status whose detail
/// contains the exception message and type - which for a database fault means leaking the server
/// name, the schema, or the connection configuration. Here the detail the client sees is fixed
/// text, and the diagnosis goes to the log with the method name to correlate on.
///
/// Having it in one place is also why the service methods contain no try/catch.

public class ExceptionInterceptor(ILogger<ExceptionInterceptor> logger) : Interceptor
{
    private const int SqlDeadlockVictim = 1205;
    private const int SqlCheckConstraintViolation = 547;

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (RpcException)
        {
            // Already a deliberate status from the service layer; pass it through untouched.
            throw;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The caller gave up or its deadline elapsed. Note that this says nothing about
            // whether the work completed: a transaction that was committing may still have
            // committed, which is exactly why the contract tells callers to reconcile.
            logger.LogInformation("{Method} was cancelled by the caller", context.Method);
            throw new RpcException(new Status(StatusCode.Cancelled, "The call was cancelled."));
        }
        catch (SqlException ex) when (ex.Number == SqlDeadlockVictim)
        {
            // Chosen as the deadlock victim. Retryable, and safe to retry with the same
            // reservation_id, so it is reported as an infrastructure problem rather than a
            // business outcome.
            logger.LogWarning(ex, "{Method} was chosen as a deadlock victim", context.Method);
            throw new RpcException(new Status(StatusCode.Unavailable,
                "The inventory service was busy; retry with the same reservation_id."));
        }
        catch (SqlException ex) when (ex.Number == SqlCheckConstraintViolation)
        {
            // A CHECK constraint stopped a write that should never have been attempted. The
            // database protected the invariant, so this is a bug on our side, not bad input.
            logger.LogError(ex, "{Method} violated a database constraint", context.Method);
            throw new RpcException(new Status(StatusCode.Internal, "An unexpected error occurred."));
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "{Method} failed with SQL error {Number}", context.Method, ex.Number);
            throw new RpcException(new Status(StatusCode.Unavailable,
                "The inventory service is temporarily unavailable."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Method} failed unexpectedly", context.Method);
            throw new RpcException(new Status(StatusCode.Internal, "An unexpected error occurred."));
        }
    }
}
