SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @TenantAlpha UNIQUEIDENTIFIER = '11111111-1111-1111-1111-111111111111';
DECLARE @TenantBeta UNIQUEIDENTIFIER = '22222222-2222-2222-2222-222222222222';
DECLARE @TenantGamma UNIQUEIDENTIFIER = '33333333-3333-3333-3333-333333333333';
DECLARE @AdminUserId UNIQUEIDENTIFIER = 'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA';
DECLARE @TenantAlphaUserId UNIQUEIDENTIFIER = 'AAAAAAAA-1111-1111-1111-111111111111';
DECLARE @TenantBetaUserId UNIQUEIDENTIFIER = 'BBBBBBBB-2222-2222-2222-222222222222';
DECLARE @TenantGammaUserId UNIQUEIDENTIFIER = 'CCCCCCCC-3333-3333-3333-333333333333';
DECLARE @SeedPasswordHash NVARCHAR(200) = N'v1.100000.AAECAwQFBgcICQoLDA0ODw==.Pj0kIvAPLMHRutBFgZv7g2ARfVnFiANcQpTzQDrAl6U=';

MERGE dbo.Tenants AS target
USING (VALUES
    (@TenantAlpha, N'Alfa Deportes', N'deportes', N'+15556689857', N'TENANT-ALFA-KEY', 1, N'{"botBehavior":{"assistantName":"Coach de Tienda","tone":"Energetico y claro","responseLanguage":"es-AR","maxResponseTokens":550},"phrases":{"welcomeMessage":"Hola, te ayudo a elegir equipamiento y talle ideal.","fallbackMessage":"Decime deporte, marca o talle y te guio.","humanHandoffMessage":"Te conecto con un asesor deportivo."},"whatsApp":{"enabled":true,"phoneNumber":"+15556689857","provider":"MetaCloud","apiKey":"TEMP_WA_TOKEN_ROTATE_ME","apiKeyUpdatedAtUtc":null}}'),
    (@TenantBeta, N'Beta Urban', N'moda', NULL, N'TENANT-BETA-KEY', 1, N'{"botBehavior":{"assistantName":"Stylist Assistant","tone":"Cercano y aspiracional","responseLanguage":"es-AR","maxResponseTokens":650},"phrases":{"welcomeMessage":"Hola, te ayudo con looks y talles.","fallbackMessage":"Contame prenda, estilo o talle para ayudarte mejor.","humanHandoffMessage":"Te conecto con un asesor de estilo."},"whatsApp":{"enabled":false,"phoneNumber":"","provider":"MetaCloud","apiKey":"","apiKeyUpdatedAtUtc":null}}'),
    (@TenantGamma, N'Gamma Outdoor', N'outdoor', NULL, N'TENANT-GAMMA-KEY', 1, N'{"botBehavior":{"assistantName":"Guia Outdoor","tone":"Tecnico y confiable","responseLanguage":"es-AR","maxResponseTokens":700},"phrases":{"welcomeMessage":"Hola, te ayudo a equiparte para aventura.","fallbackMessage":"Contame actividad, clima o duracion del viaje.","humanHandoffMessage":"Te paso con un especialista outdoor."},"whatsApp":{"enabled":false,"phoneNumber":"","provider":"MetaCloud","apiKey":"","apiKeyUpdatedAtUtc":null}}')
) AS src(Id, Name, Industry, WhatsAppNumber, ApiKey, IsActive, ConfigJson)
ON target.Id = src.Id
WHEN MATCHED THEN
    UPDATE SET Name = src.Name, Industry = src.Industry, WhatsAppNumber = src.WhatsAppNumber, ApiKey = src.ApiKey, IsActive = src.IsActive, ConfigJson = src.ConfigJson
WHEN NOT MATCHED THEN
    INSERT (Id, Name, Industry, WhatsAppNumber, ApiKey, IsActive, ConfigJson)
    VALUES (src.Id, src.Name, src.Industry, src.WhatsAppNumber, src.ApiKey, src.IsActive, src.ConfigJson);

MERGE dbo.AppUsers AS target
USING (VALUES
    (@AdminUserId, N'admin00', N'admin00@HiveOps.local', @SeedPasswordHash, N'Admin', NULL, 1),
    (@TenantAlphaUserId, N'tenant_alfa_deportes', N'tenant_alfa_deportes@HiveOps.local', @SeedPasswordHash, N'Tenant', @TenantAlpha, 1),
    (@TenantBetaUserId, N'tenant_beta_urban', N'tenant_beta_urban@HiveOps.local', @SeedPasswordHash, N'Tenant', @TenantBeta, 1),
    (@TenantGammaUserId, N'tenant_gamma_outdoor', N'tenant_gamma_outdoor@HiveOps.local', @SeedPasswordHash, N'Tenant', @TenantGamma, 1)
) AS src(Id, Username, Email, PasswordHash, Role, TenantId, IsActive)
ON target.Username = src.Username
WHEN MATCHED THEN
    UPDATE SET Email = src.Email,
               PasswordHash = src.PasswordHash,
               Role = src.Role,
               TenantId = src.TenantId,
               IsActive = src.IsActive,
               UpdatedAt = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN
    INSERT (Id, Username, Email, PasswordHash, Role, TenantId, IsActive)
    VALUES (src.Id, src.Username, src.Email, src.PasswordHash, src.Role, src.TenantId, src.IsActive);

EXEC sp_set_session_context @key=N'TenantId', @value=@TenantAlpha, @read_only=0;

DELETE FROM dbo.BusinessConfigs WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.ProductAttributeValues WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.CatalogAttributeDefinitions WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.OrderItems WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.Orders WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.Reservations WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.ConversationMessages WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.Conversations WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.Products WHERE TenantId = @TenantAlpha;

INSERT INTO dbo.BusinessConfigs
(
    Id, TenantId, OpeningHours, Branches, ShippingMethods, ReturnPolicy,
    WelcomeMessage, FallbackMessage, MaxRetryBeforeHandoff, UpdatedAt
)
VALUES
(NEWID(), @TenantAlpha, N'Lun-Vie 09:00-19:00', N'Centro|Palermo', N'Pickup|Moto', N'30 dias', N'Hola, soy Alfa Bot.', N'No llegué a entender eso.', 2, SYSDATETIMEOFFSET());

INSERT INTO dbo.Products (Id, TenantId, Name, Description, Brand, Category, Tags, Price, StockQuantity, Sku, Embedding, CreatedAt, UpdatedAt)
VALUES
('A1000000-0000-0000-0000-000000000001', @TenantAlpha, N'Zapatilla Runner Pro', N'Zapatilla running amortiguada', N'Nike', N'Running', N'running,nike,hombre', 129999.00, 18, N'ALFA-RUN-001', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
('A1000000-0000-0000-0000-000000000002', @TenantAlpha, N'Remera Dry Fit', N'Remera tecnica respirable', N'Adidas', N'Indumentaria', N'remera,training,adidas', 45999.00, 42, N'ALFA-REM-002', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
('A1000000-0000-0000-0000-000000000003', @TenantAlpha, N'Botella Termica 1L', N'Botella para entrenamiento y outdoor', N'Stanley', N'Accesorios', N'botella,termo,gym', 38999.00, 7, N'ALFA-BOT-003', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
('A1000000-0000-0000-0000-000000000004', @TenantAlpha, N'Campera Rompeviento', N'Campera ligera impermeable', N'Puma', N'Outdoor', N'campera,outdoor,viento', 89999.00, 4, N'ALFA-CAM-004', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

INSERT INTO dbo.CatalogAttributeDefinitions
(
    Id, TenantId, AttributeKey, DisplayName, DataType, IsFilterable, IsSearchable, SortOrder, CreatedAt, UpdatedAt
)
VALUES
('A5000000-0000-0000-0000-000000000001', @TenantAlpha, N'deporte', N'Deporte', N'text', 1, 1, 10, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
('A5000000-0000-0000-0000-000000000002', @TenantAlpha, N'talle', N'Talle', N'text', 1, 1, 20, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
('A5000000-0000-0000-0000-000000000003', @TenantAlpha, N'genero', N'Genero', N'text', 1, 1, 30, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
('A5000000-0000-0000-0000-000000000004', @TenantAlpha, N'material', N'Material', N'text', 1, 1, 40, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
('A5000000-0000-0000-0000-000000000005', @TenantAlpha, N'impermeable', N'Impermeable', N'boolean', 1, 1, 50, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

INSERT INTO dbo.ProductAttributeValues
(
    Id, TenantId, ProductId, AttributeKey, AttributeValue, NormalizedValue, CreatedAt, UpdatedAt
)
VALUES
(NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000001', N'deporte', N'running', N'running', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
(NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000001', N'talle', N'42', N'42', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
(NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000001', N'genero', N'hombre', N'hombre', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),

(NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000002', N'deporte', N'training', N'training', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
(NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000002', N'talle', N'M', N'm', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
(NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000002', N'genero', N'unisex', N'unisex', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
(NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000002', N'material', N'polyester', N'polyester', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),

(NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000003', N'deporte', N'fitness', N'fitness', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
(NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000003', N'material', N'acero inoxidable', N'acero inoxidable', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),

(NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000004', N'deporte', N'outdoor', N'outdoor', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
(NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000004', N'talle', N'L', N'l', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
(NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000004', N'impermeable', N'si', N'si', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

DECLARE @ConvAlpha1 UNIQUEIDENTIFIER = 'A2000000-0000-0000-0000-000000000001';
DECLARE @ConvAlpha2 UNIQUEIDENTIFIER = 'A2000000-0000-0000-0000-000000000002';
DECLARE @OrderAlpha1 UNIQUEIDENTIFIER = 'A3000000-0000-0000-0000-000000000001';
DECLARE @OrderAlpha2 UNIQUEIDENTIFIER = 'A3000000-0000-0000-0000-000000000002';

INSERT INTO dbo.Conversations (Id, TenantId, ChannelUserId, Channel, Status, FailedClassificationCount, CreatedAt, LastActivityAt)
VALUES
(@ConvAlpha1, @TenantAlpha, N'whatsapp:+549115551111', N'whatsapp', 0, 0, DATEADD(day, -2, SYSDATETIMEOFFSET()), DATEADD(hour, -2, SYSDATETIMEOFFSET())),
(@ConvAlpha2, @TenantAlpha, N'whatsapp:+549115552222', N'whatsapp', 1, 1, DATEADD(day, -1, SYSDATETIMEOFFSET()), DATEADD(minute, -30, SYSDATETIMEOFFSET()));

INSERT INTO dbo.ConversationMessages (Id, ConversationId, TenantId, Role, Content, AgentName, CreatedAt)
VALUES
(NEWID(), @ConvAlpha1, @TenantAlpha, 0, N'Tienen stock de Runner Pro?', N'RouterPlugin', DATEADD(day, -2, SYSDATETIMEOFFSET())),
(NEWID(), @ConvAlpha1, @TenantAlpha, 1, N'Si, hay 18 unidades disponibles.', N'InventoryPlugin', DATEADD(day, -2, SYSDATETIMEOFFSET())),
(NEWID(), @ConvAlpha2, @TenantAlpha, 0, N'Quiero hablar con una persona.', N'SupervisionPlugin', DATEADD(hour, -4, SYSDATETIMEOFFSET()));

INSERT INTO dbo.Orders (Id, TenantId, ConversationId, CustomerPhone, CustomerName, Status, TotalAmount, ShippingAddress, Notes, CreatedAt, UpdatedAt)
VALUES
(@OrderAlpha1, @TenantAlpha, @ConvAlpha1, N'+549115551111', N'Juan Perez', 1, 175998.00, N'Av. Siempre Viva 123', N'Entrega express', DATEADD(day, -2, SYSDATETIMEOFFSET()), DATEADD(day, -2, SYSDATETIMEOFFSET())),
(@OrderAlpha2, @TenantAlpha, @ConvAlpha1, N'+549115551111', N'Juan Perez', 3, 89999.00, N'Av. Siempre Viva 123', N'', DATEADD(day, -1, SYSDATETIMEOFFSET()), DATEADD(day, -1, SYSDATETIMEOFFSET()));

INSERT INTO dbo.OrderItems (Id, OrderId, ProductId, TenantId, ProductName, Quantity, UnitPrice, Variant)
VALUES
(NEWID(), @OrderAlpha1, 'A1000000-0000-0000-0000-000000000001', @TenantAlpha, N'Zapatilla Runner Pro', 1, 129999.00, N'Talle 42'),
(NEWID(), @OrderAlpha1, 'A1000000-0000-0000-0000-000000000002', @TenantAlpha, N'Remera Dry Fit', 1, 45999.00, N'Talle M'),
(NEWID(), @OrderAlpha2, 'A1000000-0000-0000-0000-000000000004', @TenantAlpha, N'Campera Rompeviento', 1, 89999.00, N'Talle L');

INSERT INTO dbo.Reservations (Id, TenantId, ConversationId, CustomerName, CustomerPhone, PartySize, ReservationTime, Status, Notes, ConfirmationToken, CreatedAt)
VALUES
(NEWID(), @TenantAlpha, @ConvAlpha1, N'Juan Perez', N'+549115551111', 2, DATEADD(day, 1, SYSDATETIMEOFFSET()), 1, N'Prueba tenant alfa', N'ALFARES001', SYSDATETIMEOFFSET());

EXEC sp_set_session_context @key=N'TenantId', @value=@TenantBeta, @read_only=0;

DELETE FROM dbo.BusinessConfigs WHERE TenantId = @TenantBeta;
DELETE FROM dbo.OrderItems WHERE TenantId = @TenantBeta;
DELETE FROM dbo.Orders WHERE TenantId = @TenantBeta;
DELETE FROM dbo.Reservations WHERE TenantId = @TenantBeta;
DELETE FROM dbo.ConversationMessages WHERE TenantId = @TenantBeta;
DELETE FROM dbo.Conversations WHERE TenantId = @TenantBeta;
DELETE FROM dbo.Products WHERE TenantId = @TenantBeta;

INSERT INTO dbo.BusinessConfigs
(
    Id, TenantId, OpeningHours, Branches, ShippingMethods, ReturnPolicy,
    WelcomeMessage, FallbackMessage, MaxRetryBeforeHandoff, UpdatedAt
)
VALUES
(NEWID(), @TenantBeta, N'Lun-Sab 10:00-20:00', N'Caballito|Belgrano', N'Correo|Retiro', N'15 dias', N'Hola, soy Beta Bot.', N'Podrias decirlo de otra forma?', 2, SYSDATETIMEOFFSET());

INSERT INTO dbo.Products (Id, TenantId, Name, Description, Brand, Category, Tags, Price, StockQuantity, Sku, Embedding, CreatedAt, UpdatedAt)
VALUES
('B1000000-0000-0000-0000-000000000001', @TenantBeta, N'Buzo Oversize', N'Buzo urbano premium', N'Venice', N'Indumentaria', N'buzo,urbano,oversize', 65999.00, 33, N'BETA-BUZ-001', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
('B1000000-0000-0000-0000-000000000002', @TenantBeta, N'Jean Slim Azul', N'Jean elastizado azul', N'Levis', N'Denim', N'jean,slim,azul', 82999.00, 12, N'BETA-JEA-002', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
('B1000000-0000-0000-0000-000000000003', @TenantBeta, N'Gorra Logo', N'Gorra con visera curva', N'New Era', N'Accesorios', N'gorra,urbano,logo', 24999.00, 56, N'BETA-GOR-003', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
('B1000000-0000-0000-0000-000000000004', @TenantBeta, N'Campera Denim Black', N'Campera denim negra', N'Levis', N'Denim', N'campera,denim,negra', 119999.00, 5, N'BETA-CAM-004', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

DECLARE @ConvBeta1 UNIQUEIDENTIFIER = 'B2000000-0000-0000-0000-000000000001';
DECLARE @OrderBeta1 UNIQUEIDENTIFIER = 'B3000000-0000-0000-0000-000000000001';
DECLARE @ResBeta1 UNIQUEIDENTIFIER = 'B4000000-0000-0000-0000-000000000001';

INSERT INTO dbo.Conversations (Id, TenantId, ChannelUserId, Channel, Status, FailedClassificationCount, CreatedAt, LastActivityAt)
VALUES
(@ConvBeta1, @TenantBeta, N'whatsapp:+549116661111', N'whatsapp', 0, 0, DATEADD(day, -3, SYSDATETIMEOFFSET()), DATEADD(hour, -1, SYSDATETIMEOFFSET()));

INSERT INTO dbo.ConversationMessages (Id, ConversationId, TenantId, Role, Content, AgentName, CreatedAt)
VALUES
(NEWID(), @ConvBeta1, @TenantBeta, 0, N'Busco un jean slim.', N'RouterPlugin', DATEADD(day, -3, SYSDATETIMEOFFSET())),
(NEWID(), @ConvBeta1, @TenantBeta, 1, N'Tenemos 12 unidades del Jean Slim Azul.', N'InventoryPlugin', DATEADD(day, -3, SYSDATETIMEOFFSET()));

INSERT INTO dbo.Orders (Id, TenantId, ConversationId, CustomerPhone, CustomerName, Status, TotalAmount, ShippingAddress, Notes, CreatedAt, UpdatedAt)
VALUES
(@OrderBeta1, @TenantBeta, @ConvBeta1, N'+549116661111', N'Maria Lopez', 2, 190997.00, N'Calle 9 456', N'Cliente recurrente', DATEADD(day, -3, SYSDATETIMEOFFSET()), DATEADD(day, -2, SYSDATETIMEOFFSET()));

INSERT INTO dbo.OrderItems (Id, OrderId, ProductId, TenantId, ProductName, Quantity, UnitPrice, Variant)
VALUES
(NEWID(), @OrderBeta1, 'B1000000-0000-0000-0000-000000000002', @TenantBeta, N'Jean Slim Azul', 1, 82999.00, N'Talle 30'),
(NEWID(), @OrderBeta1, 'B1000000-0000-0000-0000-000000000004', @TenantBeta, N'Campera Denim Black', 1, 119999.00, N'Talle M');

INSERT INTO dbo.Reservations (Id, TenantId, ConversationId, CustomerName, CustomerPhone, PartySize, ReservationTime, Status, Notes, ConfirmationToken, CreatedAt)
VALUES
(@ResBeta1, @TenantBeta, @ConvBeta1, N'Maria Lopez', N'+549116661111', 3, DATEADD(day, 2, SYSDATETIMEOFFSET()), 0, N'Prueba tenant beta', N'BETARES001', SYSDATETIMEOFFSET());

EXEC sp_set_session_context @key=N'TenantId', @value=@TenantGamma, @read_only=0;

DELETE FROM dbo.BusinessConfigs WHERE TenantId = @TenantGamma;
DELETE FROM dbo.OrderItems WHERE TenantId = @TenantGamma;
DELETE FROM dbo.Orders WHERE TenantId = @TenantGamma;
DELETE FROM dbo.Reservations WHERE TenantId = @TenantGamma;
DELETE FROM dbo.ConversationMessages WHERE TenantId = @TenantGamma;
DELETE FROM dbo.Conversations WHERE TenantId = @TenantGamma;
DELETE FROM dbo.Products WHERE TenantId = @TenantGamma;

INSERT INTO dbo.BusinessConfigs
(
    Id, TenantId, OpeningHours, Branches, ShippingMethods, ReturnPolicy,
    WelcomeMessage, FallbackMessage, MaxRetryBeforeHandoff, UpdatedAt
)
VALUES
(NEWID(), @TenantGamma, N'Lun-Dom 08:00-22:00', N'Rosario|Cordoba', N'Expreso|Retiro', N'10 dias', N'Hola, soy Gamma Bot.', N'Necesito un poco mas de contexto.', 3, SYSDATETIMEOFFSET());

INSERT INTO dbo.Products (Id, TenantId, Name, Description, Brand, Category, Tags, Price, StockQuantity, Sku, Embedding, CreatedAt, UpdatedAt)
VALUES
('C1000000-0000-0000-0000-000000000001', @TenantGamma, N'Mochila Trek 35L', N'Mochila outdoor de travesia', N'Deuter', N'Outdoor', N'mochila,trekking,35l', 145999.00, 9, N'GAMMA-MOC-001', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
('C1000000-0000-0000-0000-000000000002', @TenantGamma, N'Linterna Headlamp', N'Linterna frontal recargable', N'Petzl', N'Outdoor', N'linterna,headlamp,trail', 55999.00, 21, N'GAMMA-LIN-002', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
('C1000000-0000-0000-0000-000000000003', @TenantGamma, N'Campera Pluma Alpine', N'Campera termica alta montaña', N'The North Face', N'Outdoor', N'campera,pluma,alpine', 249999.00, 3, N'GAMMA-CAM-003', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

DECLARE @ConvGamma1 UNIQUEIDENTIFIER = 'C2000000-0000-0000-0000-000000000001';
DECLARE @OrderGamma1 UNIQUEIDENTIFIER = 'C3000000-0000-0000-0000-000000000001';
DECLARE @ResGamma1 UNIQUEIDENTIFIER = 'C4000000-0000-0000-0000-000000000001';

INSERT INTO dbo.Conversations (Id, TenantId, ChannelUserId, Channel, Status, FailedClassificationCount, CreatedAt, LastActivityAt)
VALUES
(@ConvGamma1, @TenantGamma, N'whatsapp:+549117771111', N'whatsapp', 0, 0, DATEADD(day, -5, SYSDATETIMEOFFSET()), DATEADD(minute, -10, SYSDATETIMEOFFSET()));

INSERT INTO dbo.ConversationMessages (Id, ConversationId, TenantId, Role, Content, AgentName, CreatedAt)
VALUES
(NEWID(), @ConvGamma1, @TenantGamma, 0, N'Necesito una mochila para trekking.', N'RouterPlugin', DATEADD(day, -5, SYSDATETIMEOFFSET())),
(NEWID(), @ConvGamma1, @TenantGamma, 1, N'Tengo la Trek 35L disponible.', N'InventoryPlugin', DATEADD(day, -5, SYSDATETIMEOFFSET()));

INSERT INTO dbo.Orders (Id, TenantId, ConversationId, CustomerPhone, CustomerName, Status, TotalAmount, ShippingAddress, Notes, CreatedAt, UpdatedAt)
VALUES
(@OrderGamma1, @TenantGamma, @ConvGamma1, N'+549117771111', N'Lucas Pereyra', 0, 145999.00, N'Av. Libertad 700', N'Prefiere retiro en tienda', DATEADD(day, -4, SYSDATETIMEOFFSET()), DATEADD(day, -4, SYSDATETIMEOFFSET()));

INSERT INTO dbo.OrderItems (Id, OrderId, ProductId, TenantId, ProductName, Quantity, UnitPrice, Variant)
VALUES
(NEWID(), @OrderGamma1, 'C1000000-0000-0000-0000-000000000001', @TenantGamma, N'Mochila Trek 35L', 1, 145999.00, N'Color grafito');

INSERT INTO dbo.Reservations (Id, TenantId, ConversationId, CustomerName, CustomerPhone, PartySize, ReservationTime, Status, Notes, ConfirmationToken, CreatedAt)
VALUES
(@ResGamma1, @TenantGamma, @ConvGamma1, N'Lucas Pereyra', N'+549117771111', 2, DATEADD(day, 3, SYSDATETIMEOFFSET()), 1, N'Prueba tenant gamma', N'GAMMARES001', SYSDATETIMEOFFSET());

EXEC sp_set_session_context @key=N'TenantId', @value=NULL, @read_only=0;
