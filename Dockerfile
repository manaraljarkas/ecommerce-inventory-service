# syntax=docker/dockerfile:1

# Build context: the repository root.  Targets:
#   final     - the Inventory gRPC API (default, last stage)
#   migrator  - one-shot job that applies the EF Core migrations, then exits
# Both come from the same source and the same pinned EF Core tooling.

# ---------------------------------------------------------------------------
# build: restore dependencies. Only the files that define dependencies are
# copied first, so this layer is reused until a package or tool version changes.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src

COPY global.json ./
COPY .config/dotnet-tools.json .config/
COPY src/InventoryService/InventoryService.csproj src/InventoryService/
RUN dotnet tool restore \
 && dotnet restore src/InventoryService/InventoryService.csproj

COPY src/InventoryService/ src/InventoryService/

# ---------------------------------------------------------------------------
# publish: the API, framework-dependent, Release.
# ---------------------------------------------------------------------------
FROM build AS publish
RUN dotnet publish src/InventoryService/InventoryService.csproj \
      --configuration Release --no-restore \
      -p:UseAppHost=false \
      --output /app/publish

# ---------------------------------------------------------------------------
# bundle: an EF Core migration bundle built from the existing migrations with
# the repository-pinned dotnet-ef (10.0.14). Self-contained, so the migrator
# image needs neither the SDK nor the source code.
# ---------------------------------------------------------------------------
FROM build AS bundle
# Program.cs refuses to start without a connection string, and EF has to start the app to
# build its model. This placeholder only satisfies that check: nothing connects while the
# bundle is being built, it is set for this one command only, and the real connection
# string is supplied when the migrator runs.
RUN ConnectionStrings__InventoryDb="Server=build-time-placeholder;Database=ECommerceInventoryDb" \
    dotnet ef migrations bundle \
      --project src/InventoryService/InventoryService.csproj \
      --configuration Release \
      --self-contained --target-runtime linux-x64 \
      --output /bundle/efbundle --force

# ---------------------------------------------------------------------------
# migrator: runs the bundle against whatever ConnectionStrings__InventoryDb
# points at, applies pending migrations only (never drops or recreates), exits.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrator
# The bundle unpacks native libraries on first run; keep that out of the image layers.
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/dotnet-bundle
WORKDIR /migrator
COPY --from=bundle --chmod=755 /bundle/efbundle ./efbundle
USER $APP_UID
ENTRYPOINT ["./efbundle"]

# ---------------------------------------------------------------------------
# final: the runtime image. ASP.NET Core runtime plus the published output only:
# no SDK, no source, no secrets. Runs as the image's built-in non-root user.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=publish /app/publish ./

# Plain-HTTP port for gRPC over cleartext HTTP/2 (h2c). Kestrel is HTTP/2-only via
# appsettings.json (Kestrel:EndpointDefaults:Protocols). TLS is expected to be
# terminated or added by the deployment, not by this development image.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "InventoryService.dll"]
