-- Migration: Add dynamic catalog attributes support
-- Purpose: Enable cross-industry catalog extensibility with tenant-safe attribute definitions and values
-- Created: 2026-04-13

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRAN;

    IF OBJECT_ID(N'dbo.CatalogAttributeDefinitions', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CatalogAttributeDefinitions
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_CatalogAttributeDefinitions PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            AttributeKey NVARCHAR(120) NOT NULL,
            DisplayName NVARCHAR(160) NOT NULL,
            DataType NVARCHAR(30) NOT NULL CONSTRAINT DF_CatalogAttributeDefinitions_DataType DEFAULT (N'text'),
            IsFilterable BIT NOT NULL CONSTRAINT DF_CatalogAttributeDefinitions_IsFilterable DEFAULT (1),
            IsSearchable BIT NOT NULL CONSTRAINT DF_CatalogAttributeDefinitions_IsSearchable DEFAULT (1),
            SortOrder INT NOT NULL CONSTRAINT DF_CatalogAttributeDefinitions_SortOrder DEFAULT (0),
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_CatalogAttributeDefinitions_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            UpdatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_CatalogAttributeDefinitions_UpdatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_CatalogAttributeDefinitions_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE CASCADE
        );

        PRINT 'Table dbo.CatalogAttributeDefinitions created.';
    END
    ELSE
    BEGIN
        PRINT 'Table dbo.CatalogAttributeDefinitions already exists.';
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.CatalogAttributeDefinitions')
          AND name = N'UX_CatalogAttributeDefinitions_Tenant_AttributeKey'
    )
    BEGIN
        CREATE UNIQUE INDEX UX_CatalogAttributeDefinitions_Tenant_AttributeKey
            ON dbo.CatalogAttributeDefinitions(TenantId, AttributeKey);
        PRINT 'Index UX_CatalogAttributeDefinitions_Tenant_AttributeKey created.';
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.CatalogAttributeDefinitions')
          AND name = N'IX_CatalogAttributeDefinitions_Tenant_SortOrder'
    )
    BEGIN
        CREATE INDEX IX_CatalogAttributeDefinitions_Tenant_SortOrder
            ON dbo.CatalogAttributeDefinitions(TenantId, SortOrder);
        PRINT 'Index IX_CatalogAttributeDefinitions_Tenant_SortOrder created.';
    END;

    IF OBJECT_ID(N'dbo.ProductAttributeValues', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.ProductAttributeValues
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ProductAttributeValues PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            ProductId UNIQUEIDENTIFIER NOT NULL,
            AttributeKey NVARCHAR(120) NOT NULL,
            AttributeValue NVARCHAR(400) NOT NULL,
            NormalizedValue NVARCHAR(400) NOT NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_ProductAttributeValues_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            UpdatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_ProductAttributeValues_UpdatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_ProductAttributeValues_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE NO ACTION,
            CONSTRAINT FK_ProductAttributeValues_Products FOREIGN KEY (ProductId) REFERENCES dbo.Products(Id) ON DELETE CASCADE
        );

        PRINT 'Table dbo.ProductAttributeValues created.';
    END
    ELSE
    BEGIN
        PRINT 'Table dbo.ProductAttributeValues already exists.';
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.ProductAttributeValues')
          AND name = N'UX_ProductAttributeValues_Tenant_Product_AttributeKey'
    )
    BEGIN
        CREATE UNIQUE INDEX UX_ProductAttributeValues_Tenant_Product_AttributeKey
            ON dbo.ProductAttributeValues(TenantId, ProductId, AttributeKey);
        PRINT 'Index UX_ProductAttributeValues_Tenant_Product_AttributeKey created.';
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.ProductAttributeValues')
          AND name = N'IX_ProductAttributeValues_Tenant_AttributeKey_NormalizedValue'
    )
    BEGIN
        CREATE INDEX IX_ProductAttributeValues_Tenant_AttributeKey_NormalizedValue
            ON dbo.ProductAttributeValues(TenantId, AttributeKey, NormalizedValue);
        PRINT 'Index IX_ProductAttributeValues_Tenant_AttributeKey_NormalizedValue created.';
    END;

    IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'TenantRlsPolicy')
       AND OBJECT_ID(N'dbo.CatalogAttributeDefinitions', N'U') IS NOT NULL
       AND NOT EXISTS (
            SELECT 1
            FROM sys.security_predicates sp
            WHERE sp.object_id = OBJECT_ID(N'dbo.TenantRlsPolicy')
              AND sp.target_object_id = OBJECT_ID(N'dbo.CatalogAttributeDefinitions')
       )
    BEGIN
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.CatalogAttributeDefinitions;');
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.CatalogAttributeDefinitions AFTER INSERT;');
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.CatalogAttributeDefinitions AFTER UPDATE;');
        PRINT 'RLS predicates added for dbo.CatalogAttributeDefinitions.';
    END;

    IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'TenantRlsPolicy')
       AND OBJECT_ID(N'dbo.ProductAttributeValues', N'U') IS NOT NULL
       AND NOT EXISTS (
            SELECT 1
            FROM sys.security_predicates sp
            WHERE sp.object_id = OBJECT_ID(N'dbo.TenantRlsPolicy')
              AND sp.target_object_id = OBJECT_ID(N'dbo.ProductAttributeValues')
       )
    BEGIN
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ProductAttributeValues;');
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ProductAttributeValues AFTER INSERT;');
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ProductAttributeValues AFTER UPDATE;');
        PRINT 'RLS predicates added for dbo.ProductAttributeValues.';
    END;

    COMMIT;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK;

    THROW;
END CATCH;
