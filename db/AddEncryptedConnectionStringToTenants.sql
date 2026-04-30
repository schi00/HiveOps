/* Idempotent: per-tenant DB connection (optional); required by DynamicConnectionStringResolver queries. */
SET NOCOUNT ON;
USE Hive;
GO

IF COL_LENGTH(N'dbo.Tenants', N'EncryptedConnectionString') IS NULL
BEGIN
    ALTER TABLE dbo.Tenants ADD EncryptedConnectionString nvarchar(max) NULL;
    PRINT 'Column EncryptedConnectionString added to Tenants.';
END
ELSE
    PRINT 'Column EncryptedConnectionString already exists.';
GO
