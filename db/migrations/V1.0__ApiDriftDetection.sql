-- ============================================================================
-- Migration: V1.0__ApiDriftDetection.sql
-- Description: Creates ApiDependencies table for API Drift Detection System
-- Author: HiveOps Engineering
-- Date: 2026-05-02
-- ============================================================================

-- Create ApiDependencies table
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ApiDependencies' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE [dbo].[ApiDependencies] (
        [Id] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY DEFAULT NEWID(),
        [TenantId] UNIQUEIDENTIFIER NOT NULL,
        [ProviderName] NVARCHAR(50) NOT NULL,
        [EndpointUrl] NVARCHAR(2048) NOT NULL,
        [HttpMethod] NVARCHAR(10) NULL DEFAULT 'GET',
        [ExpectedSchemaHash] NVARCHAR(64) NOT NULL,
        [LastResponseSchema] NVARCHAR(4000) NULL,
        [LastChecked] DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        [Status] NVARCHAR(20) NOT NULL DEFAULT 'Learning',
        [CreatedAt] DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
        [UpdatedAt] DATETIMEOFFSET NULL,
        
        -- Foreign key to Tenants table
        CONSTRAINT [FK_ApiDependencies_Tenants] FOREIGN KEY ([TenantId]) 
            REFERENCES [dbo].[Tenants]([Id]) ON DELETE CASCADE
    );
    
    PRINT 'Table [dbo].[ApiDependencies] created successfully.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[ApiDependencies] already exists. Skipping creation.';
END
GO

-- Create indexes for performance
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ApiDependencies_TenantId' AND object_id = OBJECT_ID('dbo.ApiDependencies'))
BEGIN
    CREATE INDEX [IX_ApiDependencies_TenantId] ON [dbo].[ApiDependencies]([TenantId]);
    PRINT 'Index [IX_ApiDependencies_TenantId] created successfully.';
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ApiDependencies_Tenant_Endpoint' AND object_id = OBJECT_ID('dbo.ApiDependencies'))
BEGIN
    CREATE UNIQUE INDEX [IX_ApiDependencies_Tenant_Endpoint] ON [dbo].[ApiDependencies]([TenantId], [EndpointUrl]);
    PRINT 'Index [IX_ApiDependencies_Tenant_Endpoint] created successfully.';
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ApiDependencies_Status' AND object_id = OBJECT_ID('dbo.ApiDependencies'))
BEGIN
    CREATE INDEX [IX_ApiDependencies_Status] ON [dbo].[ApiDependencies]([Status]) WHERE [Status] = 'DriftDetected';
    PRINT 'Index [IX_ApiDependencies_Status] created successfully.';
END
GO

-- Add comments for documentation
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'ApiDependencies' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    EXEC sys.sp_addextendedproperty 
        @name = N'MS_Description', 
        @value = N'Stores API dependencies for drift detection. Tracks schema hashes and alerts on contract changes.',
        @level0type = N'SCHEMA', @level0name = 'dbo',
        @level1type = N'TABLE',  @level1name = 'ApiDependencies';
    
    EXEC sys.sp_addextendedproperty 
        @name = N'MS_Description', 
        @value = N'Tenant identifier for multi-tenancy isolation',
        @level0type = N'SCHEMA', @level0name = 'dbo',
        @level1type = N'TABLE',  @level1name = 'ApiDependencies',
        @level2type = N'COLUMN', @level2name = 'TenantId';
    
    EXEC sys.sp_addextendedproperty 
        @name = N'MS_Description', 
        @value = N'Provider name: MercadoLibre, Shopify, TiendaNube, or Custom',
        @level0type = N'SCHEMA', @level0name = 'dbo',
        @level1type = N'TABLE',  @level1name = 'ApiDependencies',
        @level2type = N'COLUMN', @level2name = 'ProviderName';
    
    EXEC sys.sp_addextendedproperty 
        @name = N'MS_Description', 
        @value = N'SHA256 hash of the expected JSON schema (keys only)',
        @level0type = N'SCHEMA', @level0name = 'dbo',
        @level1type = N'TABLE',  @level1name = 'ApiDependencies',
        @level2type = N'COLUMN', @level2name = 'ExpectedSchemaHash';
    
    PRINT 'Extended properties added for documentation.';
END
GO

PRINT 'Migration V1.0__ApiDriftDetection completed successfully.';
GO
