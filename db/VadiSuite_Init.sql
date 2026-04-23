SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRY
    BEGIN TRAN;

    IF OBJECT_ID(N'dbo.Tenants', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Tenants
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Tenants PRIMARY KEY,
            Name NVARCHAR(200) NOT NULL,
            Industry NVARCHAR(100) NULL,
            WhatsAppNumber NVARCHAR(30) NULL,
            ApiKey NVARCHAR(128) NOT NULL,
            IsActive BIT NOT NULL CONSTRAINT DF_Tenants_IsActive DEFAULT (1),
            ConfigJson NVARCHAR(MAX) NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Tenants_CreatedAt DEFAULT (SYSDATETIMEOFFSET())
        );

        CREATE UNIQUE INDEX UX_Tenants_ApiKey ON dbo.Tenants(ApiKey);
        CREATE UNIQUE INDEX UX_Tenants_WhatsAppNumber ON dbo.Tenants(WhatsAppNumber) WHERE WhatsAppNumber IS NOT NULL;
        CREATE INDEX IX_Tenants_IsActive ON dbo.Tenants(IsActive);
    END;

    IF COL_LENGTH(N'dbo.Tenants', N'Industry') IS NULL
        ALTER TABLE dbo.Tenants ADD Industry NVARCHAR(100) NULL;

    IF OBJECT_ID(N'dbo.AppUsers', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.AppUsers
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_AppUsers PRIMARY KEY,
            Username NVARCHAR(100) NOT NULL,
            Email NVARCHAR(200) NOT NULL,
            PasswordHash NVARCHAR(MAX) NOT NULL,
            Role NVARCHAR(20) NOT NULL,
            TenantId UNIQUEIDENTIFIER NULL,
            IsActive BIT NOT NULL CONSTRAINT DF_AppUsers_IsActive DEFAULT (1),
            PasswordResetTokenHash NVARCHAR(128) NULL,
            PasswordResetTokenExpiresAt DATETIMEOFFSET(7) NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_AppUsers_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            UpdatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_AppUsers_UpdatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_AppUsers_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE SET NULL
        );

        CREATE UNIQUE INDEX UX_AppUsers_Username ON dbo.AppUsers(Username);
        CREATE UNIQUE INDEX UX_AppUsers_Email ON dbo.AppUsers(Email);
        CREATE INDEX IX_AppUsers_Role_IsActive ON dbo.AppUsers(Role, IsActive);
    END;

    IF OBJECT_ID(N'dbo.Products', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Products
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            Name NVARCHAR(300) NOT NULL,
            Description NVARCHAR(MAX) NOT NULL CONSTRAINT DF_Products_Description DEFAULT (N''),
            Brand NVARCHAR(200) NOT NULL CONSTRAINT DF_Products_Brand DEFAULT (N''),
            Category NVARCHAR(200) NOT NULL CONSTRAINT DF_Products_Category DEFAULT (N''),
            Tags NVARCHAR(MAX) NULL,
            Price DECIMAL(18,2) NOT NULL CONSTRAINT DF_Products_Price DEFAULT (0),
            StockQuantity INT NOT NULL CONSTRAINT DF_Products_Stock DEFAULT (0),
            Sku NVARCHAR(100) NULL,
            Embedding VECTOR(1536) NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Products_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            UpdatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Products_UpdatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_Products_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE CASCADE,
            CONSTRAINT CK_Products_Price CHECK (Price >= 0),
            CONSTRAINT CK_Products_Stock CHECK (StockQuantity >= 0)
        );

        CREATE INDEX IX_Products_TenantId ON dbo.Products(TenantId);
        CREATE INDEX IX_Products_Tenant_Sku ON dbo.Products(TenantId, Sku) WHERE Sku IS NOT NULL;
        CREATE INDEX IX_Products_Tenant_Brand ON dbo.Products(TenantId, Brand);
        CREATE INDEX IX_Products_Tenant_Name ON dbo.Products(TenantId, Name) INCLUDE (Price, StockQuantity, Brand, Sku);
    END;

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

        CREATE UNIQUE INDEX UX_CatalogAttributeDefinitions_Tenant_AttributeKey
            ON dbo.CatalogAttributeDefinitions(TenantId, AttributeKey);
        CREATE INDEX IX_CatalogAttributeDefinitions_Tenant_SortOrder
            ON dbo.CatalogAttributeDefinitions(TenantId, SortOrder);
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

        CREATE UNIQUE INDEX UX_ProductAttributeValues_Tenant_Product_AttributeKey
            ON dbo.ProductAttributeValues(TenantId, ProductId, AttributeKey);
        CREATE INDEX IX_ProductAttributeValues_Tenant_AttributeKey_NormalizedValue
            ON dbo.ProductAttributeValues(TenantId, AttributeKey, NormalizedValue);
    END;

    IF OBJECT_ID(N'dbo.Conversations', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Conversations
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Conversations PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            ChannelUserId NVARCHAR(100) NOT NULL,
            Channel NVARCHAR(50) NOT NULL,
            Status INT NOT NULL CONSTRAINT DF_Conversations_Status DEFAULT (0),
            FailedClassificationCount INT NOT NULL CONSTRAINT DF_Conversations_FailedClassificationCount DEFAULT (0),
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Conversations_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            LastActivityAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Conversations_LastActivityAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_Conversations_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE CASCADE
        );

        CREATE INDEX IX_Conversations_Tenant_ChannelUser ON dbo.Conversations(TenantId, ChannelUserId) INCLUDE (Status, LastActivityAt, Channel);
        CREATE INDEX IX_Conversations_Tenant_Status ON dbo.Conversations(TenantId, Status, LastActivityAt DESC);
    END;

    IF OBJECT_ID(N'dbo.ConversationMessages', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.ConversationMessages
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ConversationMessages PRIMARY KEY,
            ConversationId UNIQUEIDENTIFIER NOT NULL,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            Role INT NOT NULL,
            Content NVARCHAR(MAX) NOT NULL,
            AgentName NVARCHAR(100) NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_ConversationMessages_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_ConversationMessages_Conversations FOREIGN KEY (ConversationId) REFERENCES dbo.Conversations(Id) ON DELETE CASCADE,
            CONSTRAINT FK_ConversationMessages_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE NO ACTION
        );

        CREATE INDEX IX_ConversationMessages_Conv_CreatedAt ON dbo.ConversationMessages(ConversationId, CreatedAt);
        CREATE INDEX IX_ConversationMessages_Tenant_Conv_CreatedAt ON dbo.ConversationMessages(TenantId, ConversationId, CreatedAt);
    END;

    IF OBJECT_ID(N'dbo.BusinessConfigs', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.BusinessConfigs
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_BusinessConfigs PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            OpeningHours NVARCHAR(MAX) NULL,
            Branches NVARCHAR(MAX) NULL,
            ShippingMethods NVARCHAR(MAX) NULL,
            ReturnPolicy NVARCHAR(MAX) NULL,
            WelcomeMessage NVARCHAR(1000) NULL,
            FallbackMessage NVARCHAR(1000) NULL,
            MaxRetryBeforeHandoff INT NOT NULL CONSTRAINT DF_BusinessConfigs_MaxRetryBeforeHandoff DEFAULT (2),
            UpdatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_BusinessConfigs_UpdatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_BusinessConfigs_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE CASCADE,
            CONSTRAINT CK_BusinessConfigs_MaxRetryBeforeHandoff CHECK (MaxRetryBeforeHandoff BETWEEN 1 AND 10)
        );

        CREATE UNIQUE INDEX UX_BusinessConfigs_TenantId ON dbo.BusinessConfigs(TenantId);
    END;

    IF OBJECT_ID(N'dbo.Reservations', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Reservations
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Reservations PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            ConversationId UNIQUEIDENTIFIER NOT NULL,
            CustomerName NVARCHAR(200) NOT NULL,
            CustomerPhone NVARCHAR(30) NOT NULL,
            PartySize INT NOT NULL,
            ReservationTime DATETIMEOFFSET(7) NOT NULL,
            Status INT NOT NULL CONSTRAINT DF_Reservations_Status DEFAULT (0),
            Notes NVARCHAR(MAX) NULL,
            ConfirmationToken NVARCHAR(128) NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Reservations_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_Reservations_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE CASCADE,
            CONSTRAINT FK_Reservations_Conversations FOREIGN KEY (ConversationId) REFERENCES dbo.Conversations(Id) ON DELETE NO ACTION,
            CONSTRAINT CK_Reservations_PartySize CHECK (PartySize > 0)
        );

        CREATE INDEX IX_Reservations_Tenant_ReservationTime ON dbo.Reservations(TenantId, ReservationTime);
        CREATE INDEX IX_Reservations_Tenant_Conversation ON dbo.Reservations(TenantId, ConversationId, CreatedAt DESC);
        CREATE UNIQUE INDEX UX_Reservations_Tenant_ConfirmationToken ON dbo.Reservations(TenantId, ConfirmationToken) WHERE ConfirmationToken IS NOT NULL;
    END;

    IF OBJECT_ID(N'dbo.Orders', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Orders
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            ConversationId UNIQUEIDENTIFIER NOT NULL,
            CustomerPhone NVARCHAR(30) NOT NULL,
            CustomerName NVARCHAR(200) NULL,
            Status INT NOT NULL CONSTRAINT DF_Orders_Status DEFAULT (0),
            TotalAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_Orders_TotalAmount DEFAULT (0),
            ShippingAddress NVARCHAR(MAX) NULL,
            Notes NVARCHAR(MAX) NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Orders_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            UpdatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Orders_UpdatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_Orders_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE CASCADE,
            CONSTRAINT FK_Orders_Conversations FOREIGN KEY (ConversationId) REFERENCES dbo.Conversations(Id) ON DELETE NO ACTION,
            CONSTRAINT CK_Orders_TotalAmount CHECK (TotalAmount >= 0)
        );

        CREATE INDEX IX_Orders_Tenant_Status ON dbo.Orders(TenantId, Status, UpdatedAt DESC);
        CREATE INDEX IX_Orders_Tenant_Conversation_Status ON dbo.Orders(TenantId, ConversationId, Status) INCLUDE (TotalAmount, UpdatedAt);
    END;

    IF OBJECT_ID(N'dbo.OrderItems', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.OrderItems
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_OrderItems PRIMARY KEY,
            OrderId UNIQUEIDENTIFIER NOT NULL,
            ProductId UNIQUEIDENTIFIER NOT NULL,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            ProductName NVARCHAR(300) NOT NULL,
            Quantity INT NOT NULL,
            UnitPrice DECIMAL(18,2) NOT NULL,
            Variant NVARCHAR(100) NULL,
            CONSTRAINT FK_OrderItems_Orders FOREIGN KEY (OrderId) REFERENCES dbo.Orders(Id) ON DELETE CASCADE,
            CONSTRAINT FK_OrderItems_Products FOREIGN KEY (ProductId) REFERENCES dbo.Products(Id) ON DELETE NO ACTION,
            CONSTRAINT FK_OrderItems_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE NO ACTION,
            CONSTRAINT CK_OrderItems_Quantity CHECK (Quantity > 0),
            CONSTRAINT CK_OrderItems_UnitPrice CHECK (UnitPrice >= 0)
        );

        CREATE INDEX IX_OrderItems_Tenant_Order ON dbo.OrderItems(TenantId, OrderId);
        CREATE INDEX IX_OrderItems_Tenant_Product ON dbo.OrderItems(TenantId, ProductId);
    END;

    IF OBJECT_ID(N'dbo.Synonyms', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Synonyms
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Synonyms PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            Term NVARCHAR(200) NOT NULL,
            Normalized NVARCHAR(200) NOT NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Synonyms_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            UpdatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Synonyms_UpdatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_Synonyms_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE CASCADE
        );

        CREATE INDEX IX_Synonyms_Tenant ON dbo.Synonyms(TenantId);
        CREATE UNIQUE INDEX UX_Synonyms_Tenant_Term ON dbo.Synonyms(TenantId, Term);
    END;

    IF OBJECT_ID(N'dbo.Concepts', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Concepts
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Concepts PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            Name NVARCHAR(200) NOT NULL,
            [Type] NVARCHAR(80) NOT NULL CONSTRAINT DF_Concepts_Type DEFAULT (N'generic'),
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Concepts_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            UpdatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Concepts_UpdatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_Concepts_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE CASCADE
        );

        CREATE INDEX IX_Concepts_Tenant ON dbo.Concepts(TenantId);
        CREATE UNIQUE INDEX UX_Concepts_Tenant_Name ON dbo.Concepts(TenantId, Name);
        CREATE INDEX IX_Concepts_Tenant_Type ON dbo.Concepts(TenantId, [Type]);
    END;

    IF OBJECT_ID(N'dbo.ConceptProductMap', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.ConceptProductMap
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ConceptProductMap PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            ConceptId UNIQUEIDENTIFIER NOT NULL,
            Category NVARCHAR(200) NULL,
            Tags NVARCHAR(500) NULL,
            Priority INT NOT NULL CONSTRAINT DF_ConceptProductMap_Priority DEFAULT (100),
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_ConceptProductMap_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            UpdatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_ConceptProductMap_UpdatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_ConceptProductMap_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE CASCADE,
            CONSTRAINT FK_ConceptProductMap_Concepts FOREIGN KEY (ConceptId) REFERENCES dbo.Concepts(Id) ON DELETE NO ACTION
        );

        CREATE INDEX IX_ConceptProductMap_Tenant ON dbo.ConceptProductMap(TenantId);
        CREATE INDEX IX_ConceptProductMap_Tenant_Concept ON dbo.ConceptProductMap(TenantId, ConceptId);
    END;

    IF OBJECT_ID(N'dbo.fn_tenantPredicate', N'IF') IS NULL
        EXEC('
            CREATE FUNCTION dbo.fn_tenantPredicate(@TenantId UNIQUEIDENTIFIER)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS
            RETURN
            SELECT 1 AS fn_result
            WHERE TRY_CONVERT(UNIQUEIDENTIFIER, SESSION_CONTEXT(N''TenantId'')) = @TenantId;
        ');

    IF NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'TenantRlsPolicy')
    BEGIN
        EXEC(N'
            CREATE SECURITY POLICY dbo.TenantRlsPolicy
            ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Products,
            ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.CatalogAttributeDefinitions,
            ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ProductAttributeValues,
            ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Reservations,
            ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Orders,
            ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.OrderItems,
            ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Synonyms,
            ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Concepts,
            ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ConceptProductMap,
            ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.BusinessConfigs,
            ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Conversations,
            ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ConversationMessages,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Products AFTER INSERT,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Products AFTER UPDATE,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.CatalogAttributeDefinitions AFTER INSERT,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.CatalogAttributeDefinitions AFTER UPDATE,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ProductAttributeValues AFTER INSERT,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ProductAttributeValues AFTER UPDATE,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Reservations AFTER INSERT,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Reservations AFTER UPDATE,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Orders AFTER INSERT,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Orders AFTER UPDATE,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.OrderItems AFTER INSERT,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.OrderItems AFTER UPDATE,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Synonyms AFTER INSERT,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Synonyms AFTER UPDATE,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Concepts AFTER INSERT,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Concepts AFTER UPDATE,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ConceptProductMap AFTER INSERT,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ConceptProductMap AFTER UPDATE,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.BusinessConfigs AFTER INSERT,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.BusinessConfigs AFTER UPDATE,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Conversations AFTER INSERT,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Conversations AFTER UPDATE,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ConversationMessages AFTER INSERT,
            ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ConversationMessages AFTER UPDATE
            WITH (STATE = ON);
        ');
    END;

    IF OBJECT_ID(N'dbo.CatalogAttributeDefinitions', N'U') IS NOT NULL
       AND EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'TenantRlsPolicy')
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
    END;

    IF OBJECT_ID(N'dbo.ProductAttributeValues', N'U') IS NOT NULL
       AND EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'TenantRlsPolicy')
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
    END;

    IF OBJECT_ID(N'dbo.Synonyms', N'U') IS NOT NULL
       AND EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'TenantRlsPolicy')
       AND NOT EXISTS (
            SELECT 1
            FROM sys.security_predicates sp
            WHERE sp.object_id = OBJECT_ID(N'dbo.TenantRlsPolicy')
              AND sp.target_object_id = OBJECT_ID(N'dbo.Synonyms')
       )
    BEGIN
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Synonyms;');
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Synonyms AFTER INSERT;');
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Synonyms AFTER UPDATE;');
    END;

    IF OBJECT_ID(N'dbo.Concepts', N'U') IS NOT NULL
       AND EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'TenantRlsPolicy')
       AND NOT EXISTS (
            SELECT 1
            FROM sys.security_predicates sp
            WHERE sp.object_id = OBJECT_ID(N'dbo.TenantRlsPolicy')
              AND sp.target_object_id = OBJECT_ID(N'dbo.Concepts')
       )
    BEGIN
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Concepts;');
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Concepts AFTER INSERT;');
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.Concepts AFTER UPDATE;');
    END;

    IF OBJECT_ID(N'dbo.ConceptProductMap', N'U') IS NOT NULL
       AND EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'TenantRlsPolicy')
       AND NOT EXISTS (
            SELECT 1
            FROM sys.security_predicates sp
            WHERE sp.object_id = OBJECT_ID(N'dbo.TenantRlsPolicy')
              AND sp.target_object_id = OBJECT_ID(N'dbo.ConceptProductMap')
       )
    BEGIN
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD FILTER PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ConceptProductMap;');
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ConceptProductMap AFTER INSERT;');
        EXEC(N'ALTER SECURITY POLICY dbo.TenantRlsPolicy ADD BLOCK PREDICATE dbo.fn_tenantPredicate(TenantId) ON dbo.ConceptProductMap AFTER UPDATE;');
    END;

    COMMIT;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK;

    THROW;
END CATCH;

IF (FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1)
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'HiveOpsFT')
        CREATE FULLTEXT CATALOG HiveOpsFT AS DEFAULT;

    IF OBJECT_ID(N'dbo.Products', N'U') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.Products'))
    BEGIN
        CREATE FULLTEXT INDEX ON dbo.Products
        (
            Name LANGUAGE 0,
            Description LANGUAGE 0,
            Brand LANGUAGE 0,
            Tags LANGUAGE 0
        )
        KEY INDEX PK_Products
        WITH CHANGE_TRACKING AUTO;
    END;
END;
