# E-Commerce Inventory Service

## Overview

The Inventory Service is an ASP.NET Core gRPC service (.NET 10) that manages product stock for the e-commerce application. It stores its data in its own SQL Server database through Entity Framework Core. The Laravel e-commerce backend (a separate repository) is its client and communicates with it exclusively over gRPC. gRPC is its only API; the root URL returns a plain-text notice.

## Main Features

- **Stock lookup:** returns the quantity of a product that can still be reserved (physical stock minus active reservations).
- **Inventory provisioning:** creates the inventory record, with its first stock, when a product is created.
- **Reservation:** reserves the items of a complete order atomically; either every item is reserved or none is.
- **Release:** returns the quantities of a reservation to stock when an order fails to be saved or is cancelled.
- **Invalid operations are rejected:** stock cannot become negative, a released reservation cannot be reserved again, and reusing a reservation id with different items is an error.
- **Idempotency:** repeating a request with the same identifiers is safe and never applies stock changes twice.
- **Concurrency safety:** inventory rows are locked inside database transactions (`UPDLOCK, ROWLOCK`, acquired in ascending product order), so concurrent requests cannot oversell stock.

## gRPC Integration

The contract is [`src/InventoryService/Protos/inventory.proto`](src/InventoryService/Protos/inventory.proto) (package `ecommerce.inventory.v1`, service `Inventory`). It is shared by both applications. Another application generates its client from this file with `protoc` and the gRPC plugin of its language; the Laravel backend uses the PHP namespace `Ecommerce\Inventory\V1`, and the C# namespace is `ECommerce.Inventory.V1`.

| RPC | Purpose |
|---|---|
| `CreateInventoryItem` | Creates the inventory record of a new product with `initial_quantity`. Returns `CREATED`, or `ALREADY_CREATED` when repeated with the same quantity. Requires the provisioning token (see below). |
| `GetStock` | Returns `available_quantity` for a `product_id`. |
| `ReserveStock` | Reserves all `items` of an order under a caller-generated `reservation_id`. Returns `RESERVED`, `ALREADY_RESERVED` (idempotent repeat) or `INSUFFICIENT_STOCK` with the affected items. |
| `ReleaseStock` | Releases the reservation identified by `reservation_id`. Returns `RELEASED`, or `ALREADY_RELEASED` when repeated. |

Errors are returned as gRPC status codes: `INVALID_ARGUMENT` (malformed request), `NOT_FOUND` (unknown product or reservation, including a product that was never provisioned), `ALREADY_EXISTS` (conflicting repeat), `FAILED_PRECONDITION` (reservation already released, or provisioning disabled) and `UNAUTHENTICATED` (missing or wrong provisioning token). The detailed rules, including the reservation lifecycle and the responsibilities of the caller, are documented in the comments of the proto file.

**Authentication.** Only `CreateInventoryItem` is protected. The caller must send the call metadata `x-provisioning-token` with the value configured as `Provisioning__Token` in the service. If no token is configured, the RPC is refused. The other RPCs require no credentials, and the port is published on the loopback interface only.

**Transport.** The service listens for plaintext HTTP/2 (h2c) on container port 8080, published on `127.0.0.1:5275`. Clients must connect without TLS. The Laravel setting `INVENTORY_GRPC_HOST` takes `host:port`: `localhost:5275` on the Docker host, or `host.docker.internal:5275` from inside a container.

To check the connection with [grpcurl](https://github.com/fullstorydev/grpcurl):

```bash
grpcurl -plaintext -import-path src/InventoryService/Protos -proto inventory.proto \
  -d '{"product_id": 1}' localhost:5275 ecommerce.inventory.v1.Inventory/GetStock
```

The call returns `NOT_FOUND` until the product has been provisioned.

## Running the Service

### With Docker Compose

1. Create the environment file and set the values (the file is git-ignored; never commit it):

   ```bash
   cp .env.example .env
   ```

   | Variable | Description |
   |---|---|
   | `MSSQL_SA_PASSWORD` | Password of the `sa` account of the SQL Server container (required). |
   | `INVENTORY_APP_PASSWORD` | Password of `inventory_app`, the low-privilege login used by the service (required). |
   | `INVENTORY_PROVISIONING_TOKEN` | Shared secret for `CreateInventoryItem`; set the same value as `INVENTORY_PROVISIONING_TOKEN` in the Laravel application. If empty, provisioning is refused. |
   | `INVENTORY_HOST_PORT` | Published gRPC port. Default: `5275`. |
   | `ASPNETCORE_ENVIRONMENT`, `INVENTORY_SEED_ENABLED` | Optional. Demo stock for products `900001` to `900003` is inserted only in the `Development` environment with seeding enabled. |

2. Build and start the services:

   ```bash
   docker compose up -d --build
   ```

   Compose starts, in order, `sqlserver`, the one-shot `migrator` (applies the EF Core migrations), the one-shot `db-grants` (creates the `inventory_app` login) and `inventory-api`. The migrations are therefore applied automatically; the service itself never migrates the database on startup.

The service joins the Docker network `ecommerce-inventory-net`, which another Compose project can use as an external network.

### Without Docker

Provide the connection string through the `ConnectionStrings__InventoryDb` environment variable (or user secrets), apply the migrations, and start the service:

```bash
dotnet tool restore
dotnet ef database update --project src/InventoryService --connection "<connection string>"
dotnet run --project src/InventoryService
```

### Tests

```bash
dotnet test
```

The tests run against a real SQL Server. They use the server of `ConnectionStrings__InventoryDb` but create and drop their own database named `ECommerceInventoryDb_Test_<guid>`; they are skipped when no connection string is configured.

## Project Structure

| Path | Content |
|---|---|
| `src/InventoryService/Protos` | The gRPC contract |
| `src/InventoryService/Services` | gRPC service implementation and error interceptor |
| `src/InventoryService/Application` | Stock operations (reserve, release, provision) |
| `src/InventoryService/Domain` | Entities: inventory item, reservation, provisioning record |
| `src/InventoryService/Persistence` | EF Core context, configuration and migrations |
| `tests/InventoryService.Tests` | Unit, concurrency and gRPC contract tests |
| `docker/sql` | SQL script that creates the low-privilege login |
