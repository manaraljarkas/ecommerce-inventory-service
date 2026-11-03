-- Creates (or re-syncs) the low-privilege login the API connects with.
-- Idempotent: safe to run on every start. Runs AFTER the EF Core migrator, so the
-- database and tables must already exist; it fails visibly if they do not.
-- $(INVENTORY_APP_PASSWORD) is a sqlcmd scripting variable taken from the environment.
SET NOCOUNT ON;
GO

USE [ECommerceInventoryDb];
GO

IF DB_NAME() <> N'ECommerceInventoryDb' OR OBJECT_ID(N'dbo.InventoryItems') IS NULL
    THROW 50000, N'Target is not the migrated ECommerceInventoryDb database.', 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'inventory_app')
    CREATE LOGIN [inventory_app] WITH PASSWORD = N'$(INVENTORY_APP_PASSWORD)', DEFAULT_DATABASE = [ECommerceInventoryDb];
ELSE
    ALTER LOGIN [inventory_app] WITH PASSWORD = N'$(INVENTORY_APP_PASSWORD)';
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'inventory_app')
    CREATE USER [inventory_app] FOR LOGIN [inventory_app];
GO

-- Data access only. The API never alters schema; the migrator does that as sa.
ALTER ROLE db_datareader ADD MEMBER [inventory_app];
ALTER ROLE db_datawriter ADD MEMBER [inventory_app];
GO

PRINT N'inventory_app is ready on ECommerceInventoryDb.';
GO
