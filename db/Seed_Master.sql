/*
=============================================================
  VadiSuite — Master Seed Script
  Prerequisito: VadiSuite_Init.sql ya ejecutado
  Idempotente: se puede ejecutar varias veces
  Tenants: 3 | AppUsers: 4 | Products: 61 (54 Alfa + 4 Beta + 3 Gamma)
  Password todos los usuarios: 123456
=============================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @TenantAlpha       UNIQUEIDENTIFIER = '11111111-1111-1111-1111-111111111111';
DECLARE @TenantBeta        UNIQUEIDENTIFIER = '22222222-2222-2222-2222-222222222222';
DECLARE @TenantGamma       UNIQUEIDENTIFIER = '33333333-3333-3333-3333-333333333333';
DECLARE @AdminUserId       UNIQUEIDENTIFIER = 'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA';
DECLARE @TenantAlphaUserId UNIQUEIDENTIFIER = 'AAAAAAAA-1111-1111-1111-111111111111';
DECLARE @TenantBetaUserId  UNIQUEIDENTIFIER = 'BBBBBBBB-2222-2222-2222-222222222222';
DECLARE @TenantGammaUserId UNIQUEIDENTIFIER = 'CCCCCCCC-3333-3333-3333-333333333333';
DECLARE @SeedPasswordHash  NVARCHAR(200)    = N'v1.100000.AAECAwQFBgcICQoLDA0ODw==.Pj0kIvAPLMHRutBFgZv7g2ARfVnFiANcQpTzQDrAl6U=';

/* ============================================================
   1. TENANTS — MERGE (sin RLS, tabla global)
   ============================================================ */
MERGE dbo.Tenants AS target
USING (VALUES
    (@TenantAlpha, N'Alfa Deportes', N'deportes', N'+15556689857', N'TENANT-ALFA-KEY', 1,
     N'{"botBehavior":{"assistantName":"Coach de Tienda","tone":"Energetico y claro","responseLanguage":"es-AR","maxResponseTokens":550},"phrases":{"welcomeMessage":"Hola, te ayudo a elegir equipamiento y talle ideal.","fallbackMessage":"Decime deporte, marca o talle y te guio.","humanHandoffMessage":"Te conecto con un asesor deportivo."},"whatsApp":{"enabled":true,"phoneNumber":"+15556689857","provider":"MetaCloud","apiKey":"TEMP_WA_TOKEN_ROTATE_ME","apiKeyUpdatedAtUtc":null}}'),
    (@TenantBeta, N'Beta Urban', N'moda', NULL, N'TENANT-BETA-KEY', 1,
     N'{"botBehavior":{"assistantName":"Stylist Assistant","tone":"Cercano y aspiracional","responseLanguage":"es-AR","maxResponseTokens":650},"phrases":{"welcomeMessage":"Hola, te ayudo con looks y talles.","fallbackMessage":"Contame prenda, estilo o talle para ayudarte mejor.","humanHandoffMessage":"Te conecto con un asesor de estilo."},"whatsApp":{"enabled":false,"phoneNumber":"","provider":"MetaCloud","apiKey":"","apiKeyUpdatedAtUtc":null}}'),
    (@TenantGamma, N'Gamma Outdoor', N'outdoor', NULL, N'TENANT-GAMMA-KEY', 1,
     N'{"botBehavior":{"assistantName":"Guia Outdoor","tone":"Tecnico y confiable","responseLanguage":"es-AR","maxResponseTokens":700},"phrases":{"welcomeMessage":"Hola, te ayudo a equiparte para aventura.","fallbackMessage":"Contame actividad, clima o duracion del viaje.","humanHandoffMessage":"Te paso con un especialista outdoor."},"whatsApp":{"enabled":false,"phoneNumber":"","provider":"MetaCloud","apiKey":"","apiKeyUpdatedAtUtc":null}}')
) AS src(Id, Name, Industry, WhatsAppNumber, ApiKey, IsActive, ConfigJson)
ON target.Id = src.Id
WHEN MATCHED THEN
    UPDATE SET Name           = src.Name,
               Industry       = src.Industry,
               WhatsAppNumber = src.WhatsAppNumber,
               ApiKey         = src.ApiKey,
               IsActive       = src.IsActive,
               ConfigJson     = src.ConfigJson
WHEN NOT MATCHED THEN
    INSERT (Id, Name, Industry, WhatsAppNumber, ApiKey, IsActive, ConfigJson)
    VALUES (src.Id, src.Name, src.Industry, src.WhatsAppNumber, src.ApiKey, src.IsActive, src.ConfigJson);

/* ============================================================
   2. APP USERS — MERGE (sin RLS, tabla global)
      Password: 123456
   ============================================================ */
MERGE dbo.AppUsers AS target
USING (VALUES
    (@AdminUserId,       N'admin00',             N'admin00@saasbot.local',             @SeedPasswordHash, N'Admin',  NULL,         1),
    (@TenantAlphaUserId, N'tenant_alfa_deportes', N'tenant_alfa_deportes@saasbot.local', @SeedPasswordHash, N'Tenant', @TenantAlpha, 1),
    (@TenantBetaUserId,  N'tenant_beta_urban',    N'tenant_beta_urban@saasbot.local',    @SeedPasswordHash, N'Tenant', @TenantBeta,  1),
    (@TenantGammaUserId, N'tenant_gamma_outdoor', N'tenant_gamma_outdoor@saasbot.local', @SeedPasswordHash, N'Tenant', @TenantGamma, 1)
) AS src(Id, Username, Email, PasswordHash, Role, TenantId, IsActive)
ON target.Username = src.Username
WHEN MATCHED THEN
    UPDATE SET Email      = src.Email,
               PasswordHash = src.PasswordHash,
               Role        = src.Role,
               TenantId    = src.TenantId,
               IsActive    = src.IsActive,
               UpdatedAt   = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN
    INSERT (Id, Username, Email, PasswordHash, Role, TenantId, IsActive)
    VALUES (src.Id, src.Username, src.Email, src.PasswordHash, src.Role, src.TenantId, src.IsActive);

/* ============================================================
   3. ALFA DEPORTES (TenantAlpha) — datos con RLS activo
   ============================================================ */
EXEC sp_set_session_context @key = N'TenantId', @value = @TenantAlpha, @read_only = 0;

-- Limpia en orden seguro respecto a FK
DELETE FROM dbo.BusinessConfigs             WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.ProductAttributeValues      WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.CatalogAttributeDefinitions WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.OrderItems                  WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.Orders                      WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.Reservations                WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.ConversationMessages        WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.Conversations               WHERE TenantId = @TenantAlpha;
DELETE FROM dbo.Products                    WHERE TenantId = @TenantAlpha;

-- BusinessConfig Alpha
INSERT INTO dbo.BusinessConfigs
    (Id, TenantId, OpeningHours, Branches, ShippingMethods, ReturnPolicy,
     WelcomeMessage, FallbackMessage, MaxRetryBeforeHandoff, UpdatedAt)
VALUES
    (NEWID(), @TenantAlpha,
     N'Lun-Vie 09:00-19:00, Sab 09:00-14:00',
     N'Centro|Palermo|Caballito',
     N'Pickup|Moto|Correo',
     N'30 dias con ticket',
     N'Hola, soy Coach de Tienda. Te ayudo a elegir equipamiento y talle ideal.',
     N'Decime deporte, marca o talle y te guio.',
     2, SYSDATETIMEOFFSET());

-- Products Alpha: 4 basicos con atributos + 50 catalogo completo = 54 articulos
INSERT INTO dbo.Products
    (Id, TenantId, Name, Description, Brand, Category, Tags, Price, StockQuantity, Sku, Embedding, CreatedAt, UpdatedAt)
VALUES
    -- 4 productos basicos (referenciados por ProductAttributeValues)
    ('A1000000-0000-0000-0000-000000000001', @TenantAlpha,
     N'Zapatilla Runner Pro', N'Zapatilla running amortiguada para entrenamiento diario.',
     N'Nike', N'Running', N'running,nike,hombre,amortiguacion', 129999.00, 18, N'ALFA-RUN-001', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('A1000000-0000-0000-0000-000000000002', @TenantAlpha,
     N'Remera Dry Fit', N'Remera tecnica respirable de secado rapido.',
     N'Adidas', N'Indumentaria', N'remera,training,adidas,dry-fit', 45999.00, 42, N'ALFA-REM-002', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('A1000000-0000-0000-0000-000000000003', @TenantAlpha,
     N'Botella Termica 1L', N'Botella acero inoxidable para entrenamiento y outdoor.',
     N'Stanley', N'Accesorios', N'botella,termo,gym,hidratacion', 38999.00, 7, N'ALFA-BOT-003', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('A1000000-0000-0000-0000-000000000004', @TenantAlpha,
     N'Campera Rompeviento', N'Campera ligera impermeable ideal para running al aire libre.',
     N'Puma', N'Outdoor', N'campera,outdoor,viento,impermeable', 89999.00, 4, N'ALFA-CAM-004', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    -- 50 productos del catalogo completo Alfa Deportes
    ('10000000-0000-0000-0000-000000000001', @TenantAlpha,
     N'Zapatillas Nike Pegasus 41', N'Calzado de running liviano con amortiguacion reactiva.',
     N'Nike', N'Calzado Running', N'nike,running,zapatillas,pegasus', 134990.00, 14, N'ALFA-NK-001', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000002', @TenantAlpha,
     N'Zapatillas Adidas Supernova Rise', N'Zapatillas para entrenamiento diario con buena estabilidad.',
     N'Adidas', N'Calzado Running', N'adidas,running,supernova', 129990.00, 11, N'ALFA-AD-002', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000003', @TenantAlpha,
     N'Zapatillas Puma Velocity Nitro 3', N'Modelo de running con espuma Nitro para distancias medias.',
     N'Puma', N'Calzado Running', N'puma,running,velocity', 124990.00, 9, N'ALFA-PM-003', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000004', @TenantAlpha,
     N'Botines Adidas Predator League', N'Botines para cesped sintetico con control optimizado.',
     N'Adidas', N'Futbol', N'futbol,botines,adidas,predator', 109990.00, 18, N'ALFA-AD-004', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000005', @TenantAlpha,
     N'Botines Nike Tiempo Legend 10', N'Botines de futbol con capellada suave y gran ajuste.',
     N'Nike', N'Futbol', N'futbol,botines,nike,tiempo', 119990.00, 15, N'ALFA-NK-005', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000006', @TenantAlpha,
     N'Pelota Adidas Al Rihla Training', N'Pelota de entrenamiento resistente para cesped y sintetico.',
     N'Adidas', N'Futbol', N'pelota,futbol,adidas', 32990.00, 27, N'ALFA-AD-006', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000007', @TenantAlpha,
     N'Pelota Nike Academy Team', N'Pelota de futbol cosida a maquina para uso frecuente.',
     N'Nike', N'Futbol', N'pelota,futbol,nike', 29990.00, 24, N'ALFA-NK-007', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000008', @TenantAlpha,
     N'Remera Adidas Own The Run', N'Remera tecnica respirable para running y gimnasio.',
     N'Adidas', N'Ropa Deportiva', N'remera,adidas,running', 24990.00, 34, N'ALFA-AD-008', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000009', @TenantAlpha,
     N'Remera Nike Miler Dri-FIT', N'Remera de secado rapido para entrenamientos intensos.',
     N'Nike', N'Ropa Deportiva', N'remera,nike,dri-fit', 25990.00, 31, N'ALFA-NK-009', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000010', @TenantAlpha,
     N'Remera Puma Train Favorite', N'Remera liviana para gimnasio y funcional.',
     N'Puma', N'Ropa Deportiva', N'remera,puma,training', 21990.00, 29, N'ALFA-PM-010', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000011', @TenantAlpha,
     N'Short Adidas Aeroready', N'Short deportivo con cintura elastica y tejido liviano.',
     N'Adidas', N'Ropa Deportiva', N'short,adidas,aeroready', 21990.00, 26, N'ALFA-AD-011', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000012', @TenantAlpha,
     N'Short Nike Challenger 7', N'Short de running con slip interno y bolsillos laterales.',
     N'Nike', N'Ropa Deportiva', N'short,nike,running', 23990.00, 22, N'ALFA-NK-012', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000013', @TenantAlpha,
     N'Short Under Armour Launch', N'Short elastico para running y entrenamiento.',
     N'Under Armour', N'Ropa Deportiva', N'short,under armour,launch', 26990.00, 20, N'ALFA-UA-013', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000014', @TenantAlpha,
     N'Campera Rompeviento Nike Windrunner', N'Campera liviana para correr en dias ventosos.',
     N'Nike', N'Abrigos', N'campera,nike,windrunner', 89990.00, 13, N'ALFA-NK-014', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000015', @TenantAlpha,
     N'Campera Adidas Tiro 24', N'Campera deportiva para entrenamiento y uso urbano.',
     N'Adidas', N'Abrigos', N'campera,adidas,tiro', 84990.00, 12, N'ALFA-AD-015', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000016', @TenantAlpha,
     N'Buzo Puma Essentials Logo', N'Buzo frizado de uso diario con capucha.',
     N'Puma', N'Abrigos', N'buzo,puma,hoodie', 55990.00, 17, N'ALFA-PM-016', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000017', @TenantAlpha,
     N'Buzo Under Armour Rival Fleece', N'Buzo comodo para entrada en calor y post entrenamiento.',
     N'Under Armour', N'Abrigos', N'buzo,under armour,fleece', 61990.00, 16, N'ALFA-UA-017', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000018', @TenantAlpha,
     N'Calza Adidas Techfit Mujer', N'Calza de compresion para training y running.',
     N'Adidas', N'Ropa Deportiva', N'calza,adidas,techfit', 34990.00, 21, N'ALFA-AD-018', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000019', @TenantAlpha,
     N'Calza Nike One Mujer', N'Calza elastica de tiro alto para gimnasio.',
     N'Nike', N'Ropa Deportiva', N'calza,nike,mujer', 38990.00, 19, N'ALFA-NK-019', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000020', @TenantAlpha,
     N'Calza Puma Studio Ultrabare', N'Calza suave y flexible para yoga y entrenamiento.',
     N'Puma', N'Ropa Deportiva', N'calza,puma,yoga', 35990.00, 14, N'ALFA-PM-020', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000021', @TenantAlpha,
     N'Mochila Adidas Classic Badge', N'Mochila urbana con compartimento principal amplio.',
     N'Adidas', N'Accesorios', N'mochila,adidas,urbana', 32990.00, 23, N'ALFA-AD-021', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000022', @TenantAlpha,
     N'Mochila Nike Brasilia 9.5', N'Mochila resistente para entrenamiento y estudio.',
     N'Nike', N'Accesorios', N'mochila,nike,brasilia', 37990.00, 20, N'ALFA-NK-022', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000023', @TenantAlpha,
     N'Bolso Puma Fundamentals Sports', N'Bolso mediano para gimnasio con correa regulable.',
     N'Puma', N'Accesorios', N'bolso,puma,gym', 34990.00, 18, N'ALFA-PM-023', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000024', @TenantAlpha,
     N'Rinonera Under Armour Loudon', N'Rinonera compacta para llevar objetos esenciales.',
     N'Under Armour', N'Accesorios', N'rinonera,under armour', 21990.00, 25, N'ALFA-UA-024', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000025', @TenantAlpha,
     N'Guantes de Gimnasio Adidas Essential', N'Guantes con palma reforzada para musculacion.',
     N'Adidas', N'Equipamiento Gym', N'guantes,gym,adidas', 18990.00, 28, N'ALFA-AD-025', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000026', @TenantAlpha,
     N'Guantes de Gimnasio Nike Extreme', N'Guantes transpirables para pesas y entrenamiento funcional.',
     N'Nike', N'Equipamiento Gym', N'guantes,gym,nike', 20990.00, 24, N'ALFA-NK-026', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000027', @TenantAlpha,
     N'Colchoneta Fitness Domyos Comfort', N'Colchoneta de 10 mm para yoga y abdominales.',
     N'Domyos', N'Equipamiento Gym', N'colchoneta,yoga,fitness', 27990.00, 18, N'ALFA-DM-027', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000028', @TenantAlpha,
     N'Bandas Elasticas Set Training', N'Set de bandas de resistencia para entrenamiento en casa.',
     N'Everlast', N'Equipamiento Gym', N'bandas,resistencia,training', 19990.00, 32, N'ALFA-EV-028', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000029', @TenantAlpha,
     N'Mancuerna Hexagonal 10 kg', N'Mancuerna engomada para entrenamiento funcional.',
     N'Body Sculpture', N'Equipamiento Gym', N'mancuerna,pesas,gym', 28990.00, 10, N'ALFA-BS-029', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000030', @TenantAlpha,
     N'Mancuerna Hexagonal 15 kg', N'Mancuerna engomada para fuerza y musculacion.',
     N'Body Sculpture', N'Equipamiento Gym', N'mancuerna,pesas,fuerza', 39990.00, 8, N'ALFA-BS-030', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000031', @TenantAlpha,
     N'Rodillera Nike Pro Open Patella', N'Rodillera de soporte para entrenamiento y running.',
     N'Nike', N'Accesorios', N'rodillera,nike,soporte', 24990.00, 14, N'ALFA-NK-031', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000032', @TenantAlpha,
     N'Tobillera Adidas Support', N'Tobillera elastica para estabilizacion ligera.',
     N'Adidas', N'Accesorios', N'tobillera,adidas,soporte', 17990.00, 16, N'ALFA-AD-032', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000033', @TenantAlpha,
     N'Media Nike Everyday Cushioned x3', N'Pack de medias deportivas acolchadas.',
     N'Nike', N'Accesorios', N'medias,nike,pack', 14990.00, 38, N'ALFA-NK-033', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000034', @TenantAlpha,
     N'Media Adidas Cushioned Crew x3', N'Pack de medias altas para training y uso diario.',
     N'Adidas', N'Accesorios', N'medias,adidas,pack', 13990.00, 36, N'ALFA-AD-034', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000035', @TenantAlpha,
     N'Gorra Puma Metal Cat', N'Gorra regulable para running y uso casual.',
     N'Puma', N'Accesorios', N'gorra,puma,training', 16990.00, 22, N'ALFA-PM-035', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000036', @TenantAlpha,
     N'Gorra Under Armour Blitzing', N'Gorra elastica de secado rapido.',
     N'Under Armour', N'Accesorios', N'gorra,under armour', 19990.00, 19, N'ALFA-UA-036', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000037', @TenantAlpha,
     N'Botella Adidas Steel 750 ml', N'Botella termica para gimnasio y aire libre.',
     N'Adidas', N'Accesorios', N'botella,adidas,hidratacion', 15990.00, 30, N'ALFA-AD-037', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000038', @TenantAlpha,
     N'Botella Nike Hypercharge 950 ml', N'Botella deportiva con tapa a rosca.',
     N'Nike', N'Accesorios', N'botella,nike,hidratacion', 16990.00, 28, N'ALFA-NK-038', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000039', @TenantAlpha,
     N'Auriculares JBL Endurance Run 2', N'Auriculares in-ear deportivos resistentes al sudor.',
     N'JBL', N'Tecnologia Deportiva', N'auriculares,jbl,deporte', 45990.00, 12, N'ALFA-JB-039', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000040', @TenantAlpha,
     N'Smartwatch Garmin Forerunner 55', N'Reloj GPS para running con metricas basicas.',
     N'Garmin', N'Tecnologia Deportiva', N'smartwatch,garmin,running', 249990.00, 5, N'ALFA-GR-040', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000041', @TenantAlpha,
     N'Pechera de Entrenamiento Set x10', N'Juego de pecheras para practicas de futbol.',
     N'Penalty', N'Futbol', N'pechera,futbol,equipo', 42990.00, 7, N'ALFA-PN-041', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000042', @TenantAlpha,
     N'Conos de Entrenamiento Set x20', N'Conos flexibles para ejercicios de velocidad.',
     N'DRB', N'Futbol', N'conos,entrenamiento,futbol', 24990.00, 11, N'ALFA-DR-042', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000043', @TenantAlpha,
     N'Raqueta Wilson Pro Staff Team', N'Raqueta liviana para jugadores intermedios.',
     N'Wilson', N'Tenis', N'tenis,raqueta,wilson', 169990.00, 6, N'ALFA-WL-043', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000044', @TenantAlpha,
     N'Pelotas Wilson Championship x3', N'Tubo de pelotas de tenis para entrenamiento.',
     N'Wilson', N'Tenis', N'tenis,pelotas,wilson', 14990.00, 21, N'ALFA-WL-044', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000045', @TenantAlpha,
     N'Raqueta Head Spark Elite', N'Raqueta para principiantes con buen control.',
     N'Head', N'Tenis', N'tenis,raqueta,head', 119990.00, 7, N'ALFA-HD-045', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000046', @TenantAlpha,
     N'Bicicleta Fija Randers ARG-134', N'Bicicleta fija magnetica para cardio en casa.',
     N'Randers', N'Equipamiento Cardio', N'bicicleta,cardio,fitness', 459990.00, 4, N'ALFA-RD-046', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000047', @TenantAlpha,
     N'Soga de Saltar Everlast Speed', N'Soga regulable para boxeo y entrenamiento funcional.',
     N'Everlast', N'Equipamiento Cardio', N'soga,saltar,cardio', 12990.00, 29, N'ALFA-EV-047', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000048', @TenantAlpha,
     N'Guantes de Boxeo Everlast 12 oz', N'Guantes para bolsa y entrenamiento tecnico.',
     N'Everlast', N'Boxeo', N'boxeo,guantes,everlast', 49990.00, 13, N'ALFA-EV-048', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000049', @TenantAlpha,
     N'Protector Bucal Shock Doctor', N'Protector bucal para deportes de contacto.',
     N'Shock Doctor', N'Boxeo', N'boxeo,protector,bucal', 9990.00, 35, N'ALFA-SD-049', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('10000000-0000-0000-0000-000000000050', @TenantAlpha,
     N'Bolsa de Arena Everlast 25 kg', N'Bolsa de boxeo para entrenamiento de potencia.',
     N'Everlast', N'Boxeo', N'boxeo,bolsa,entrenamiento', 139990.00, 5, N'ALFA-EV-050', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

-- CatalogAttributeDefinitions Alpha (5 atributos)
INSERT INTO dbo.CatalogAttributeDefinitions
    (Id, TenantId, AttributeKey, DisplayName, DataType, IsFilterable, IsSearchable, SortOrder, CreatedAt, UpdatedAt)
VALUES
    ('A5000000-0000-0000-0000-000000000001', @TenantAlpha, N'deporte',      N'Deporte',      N'text',    1, 1, 10, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('A5000000-0000-0000-0000-000000000002', @TenantAlpha, N'talle',        N'Talle',        N'text',    1, 1, 20, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('A5000000-0000-0000-0000-000000000003', @TenantAlpha, N'genero',       N'Genero',       N'text',    1, 1, 30, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('A5000000-0000-0000-0000-000000000004', @TenantAlpha, N'material',     N'Material',     N'text',    1, 1, 40, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('A5000000-0000-0000-0000-000000000005', @TenantAlpha, N'impermeable',  N'Impermeable',  N'boolean', 1, 1, 50, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

-- ProductAttributeValues Alpha (12 valores para los 4 productos basicos)
INSERT INTO dbo.ProductAttributeValues
    (Id, TenantId, ProductId, AttributeKey, AttributeValue, NormalizedValue, CreatedAt, UpdatedAt)
VALUES
    -- Zapatilla Runner Pro
    (NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000001', N'deporte',  N'running',           N'running',           SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    (NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000001', N'talle',    N'42',                N'42',                SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    (NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000001', N'genero',   N'hombre',            N'hombre',            SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    -- Remera Dry Fit
    (NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000002', N'deporte',  N'training',          N'training',          SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    (NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000002', N'talle',    N'M',                 N'm',                 SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    (NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000002', N'genero',   N'unisex',            N'unisex',            SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    (NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000002', N'material', N'polyester',         N'polyester',         SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    -- Botella Termica 1L
    (NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000003', N'deporte',  N'fitness',           N'fitness',           SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    (NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000003', N'material', N'acero inoxidable',  N'acero inoxidable',  SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    -- Campera Rompeviento
    (NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000004', N'deporte',      N'outdoor', N'outdoor', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    (NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000004', N'talle',        N'L',       N'l',       SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    (NEWID(), @TenantAlpha, 'A1000000-0000-0000-0000-000000000004', N'impermeable',  N'si',      N'si',      SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

-- Conversations Alpha
DECLARE @ConvAlpha1 UNIQUEIDENTIFIER = 'A2000000-0000-0000-0000-000000000001';
DECLARE @ConvAlpha2 UNIQUEIDENTIFIER = 'A2000000-0000-0000-0000-000000000002';
DECLARE @OrderAlpha1 UNIQUEIDENTIFIER = 'A3000000-0000-0000-0000-000000000001';
DECLARE @OrderAlpha2 UNIQUEIDENTIFIER = 'A3000000-0000-0000-0000-000000000002';

INSERT INTO dbo.Conversations
    (Id, TenantId, ChannelUserId, Channel, Status, FailedClassificationCount, CreatedAt, LastActivityAt)
VALUES
    (@ConvAlpha1, @TenantAlpha, N'whatsapp:+549115551111', N'whatsapp', 0, 0,
     DATEADD(day, -2, SYSDATETIMEOFFSET()), DATEADD(hour, -2, SYSDATETIMEOFFSET())),
    (@ConvAlpha2, @TenantAlpha, N'whatsapp:+549115552222', N'whatsapp', 1, 1,
     DATEADD(day, -1, SYSDATETIMEOFFSET()), DATEADD(minute, -30, SYSDATETIMEOFFSET()));

INSERT INTO dbo.ConversationMessages
    (Id, ConversationId, TenantId, Role, Content, AgentName, CreatedAt)
VALUES
    (NEWID(), @ConvAlpha1, @TenantAlpha, 0, N'Tienen stock de Runner Pro?',          N'RouterPlugin',     DATEADD(day, -2, SYSDATETIMEOFFSET())),
    (NEWID(), @ConvAlpha1, @TenantAlpha, 1, N'Si, hay 18 unidades disponibles.',     N'InventoryPlugin',  DATEADD(day, -2, SYSDATETIMEOFFSET())),
    (NEWID(), @ConvAlpha2, @TenantAlpha, 0, N'Quiero hablar con una persona.',       N'SupervisionPlugin',DATEADD(hour, -4, SYSDATETIMEOFFSET()));

INSERT INTO dbo.Orders
    (Id, TenantId, ConversationId, CustomerPhone, CustomerName, Status, TotalAmount,
     ShippingAddress, Notes, CreatedAt, UpdatedAt)
VALUES
    (@OrderAlpha1, @TenantAlpha, @ConvAlpha1, N'+549115551111', N'Juan Perez',  1, 175998.00,
     N'Av. Siempre Viva 123', N'Entrega express', DATEADD(day, -2, SYSDATETIMEOFFSET()), DATEADD(day, -2, SYSDATETIMEOFFSET())),
    (@OrderAlpha2, @TenantAlpha, @ConvAlpha1, N'+549115551111', N'Juan Perez',  3, 89999.00,
     N'Av. Siempre Viva 123', N'',                DATEADD(day, -1, SYSDATETIMEOFFSET()), DATEADD(day, -1, SYSDATETIMEOFFSET()));

INSERT INTO dbo.OrderItems
    (Id, OrderId, ProductId, TenantId, ProductName, Quantity, UnitPrice, Variant)
VALUES
    (NEWID(), @OrderAlpha1, 'A1000000-0000-0000-0000-000000000001', @TenantAlpha, N'Zapatilla Runner Pro', 1, 129999.00, N'Talle 42'),
    (NEWID(), @OrderAlpha1, 'A1000000-0000-0000-0000-000000000002', @TenantAlpha, N'Remera Dry Fit',       1,  45999.00, N'Talle M'),
    (NEWID(), @OrderAlpha2, 'A1000000-0000-0000-0000-000000000004', @TenantAlpha, N'Campera Rompeviento',  1,  89999.00, N'Talle L');

INSERT INTO dbo.Reservations
    (Id, TenantId, ConversationId, CustomerName, CustomerPhone, PartySize,
     ReservationTime, Status, Notes, ConfirmationToken, CreatedAt)
VALUES
    (NEWID(), @TenantAlpha, @ConvAlpha1, N'Juan Perez', N'+549115551111', 2,
     DATEADD(day, 1, SYSDATETIMEOFFSET()), 1, N'Prueba tenant alfa', N'ALFARES001', SYSDATETIMEOFFSET());

/* ============================================================
   4. BETA URBAN (TenantBeta) — datos con RLS activo
   ============================================================ */
EXEC sp_set_session_context @key = N'TenantId', @value = @TenantBeta, @read_only = 0;

DELETE FROM dbo.BusinessConfigs         WHERE TenantId = @TenantBeta;
DELETE FROM dbo.OrderItems              WHERE TenantId = @TenantBeta;
DELETE FROM dbo.Orders                  WHERE TenantId = @TenantBeta;
DELETE FROM dbo.Reservations            WHERE TenantId = @TenantBeta;
DELETE FROM dbo.ConversationMessages    WHERE TenantId = @TenantBeta;
DELETE FROM dbo.Conversations           WHERE TenantId = @TenantBeta;
DELETE FROM dbo.Products                WHERE TenantId = @TenantBeta;

-- BusinessConfig Beta
INSERT INTO dbo.BusinessConfigs
    (Id, TenantId, OpeningHours, Branches, ShippingMethods, ReturnPolicy,
     WelcomeMessage, FallbackMessage, MaxRetryBeforeHandoff, UpdatedAt)
VALUES
    (NEWID(), @TenantBeta,
     N'Lun-Sab 10:00-20:00',
     N'Caballito|Belgrano|Recoleta',
     N'Correo|Retiro|Moto',
     N'15 dias con etiqueta',
     N'Hola, soy Stylist Assistant. Te ayudo con looks y talles.',
     N'Contame prenda, estilo o talle para ayudarte mejor.',
     2, SYSDATETIMEOFFSET());

-- Products Beta (4 articulos moda urbana)
INSERT INTO dbo.Products
    (Id, TenantId, Name, Description, Brand, Category, Tags, Price, StockQuantity, Sku, Embedding, CreatedAt, UpdatedAt)
VALUES
    ('B1000000-0000-0000-0000-000000000001', @TenantBeta,
     N'Buzo Oversize', N'Buzo urbano premium con capucha y corte amplio.',
     N'Venice', N'Indumentaria', N'buzo,urbano,oversize,premium', 65999.00, 33, N'BETA-BUZ-001', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('B1000000-0000-0000-0000-000000000002', @TenantBeta,
     N'Jean Slim Azul', N'Jean elastizado azul de corte slim.',
     N'Levis', N'Denim', N'jean,slim,azul,elastizado', 82999.00, 12, N'BETA-JEA-002', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('B1000000-0000-0000-0000-000000000003', @TenantBeta,
     N'Gorra Logo', N'Gorra con visera curva y logo bordado.',
     N'New Era', N'Accesorios', N'gorra,urbano,logo,new era', 24999.00, 56, N'BETA-GOR-003', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('B1000000-0000-0000-0000-000000000004', @TenantBeta,
     N'Campera Denim Black', N'Campera denim negra con botones y bolsillos.',
     N'Levis', N'Denim', N'campera,denim,negra,levis', 119999.00, 5, N'BETA-CAM-004', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

-- Conversations Beta
DECLARE @ConvBeta1  UNIQUEIDENTIFIER = 'B2000000-0000-0000-0000-000000000001';
DECLARE @OrderBeta1 UNIQUEIDENTIFIER = 'B3000000-0000-0000-0000-000000000001';

INSERT INTO dbo.Conversations
    (Id, TenantId, ChannelUserId, Channel, Status, FailedClassificationCount, CreatedAt, LastActivityAt)
VALUES
    (@ConvBeta1, @TenantBeta, N'whatsapp:+549116661111', N'whatsapp', 0, 0,
     DATEADD(day, -3, SYSDATETIMEOFFSET()), DATEADD(hour, -1, SYSDATETIMEOFFSET()));

INSERT INTO dbo.ConversationMessages
    (Id, ConversationId, TenantId, Role, Content, AgentName, CreatedAt)
VALUES
    (NEWID(), @ConvBeta1, @TenantBeta, 0, N'Busco un jean slim.',                         N'RouterPlugin',    DATEADD(day, -3, SYSDATETIMEOFFSET())),
    (NEWID(), @ConvBeta1, @TenantBeta, 1, N'Tenemos 12 unidades del Jean Slim Azul.',     N'InventoryPlugin', DATEADD(day, -3, SYSDATETIMEOFFSET()));

INSERT INTO dbo.Orders
    (Id, TenantId, ConversationId, CustomerPhone, CustomerName, Status, TotalAmount,
     ShippingAddress, Notes, CreatedAt, UpdatedAt)
VALUES
    (@OrderBeta1, @TenantBeta, @ConvBeta1, N'+549116661111', N'Maria Lopez', 2, 202998.00,
     N'Calle 9 456', N'Cliente recurrente', DATEADD(day, -3, SYSDATETIMEOFFSET()), DATEADD(day, -2, SYSDATETIMEOFFSET()));

INSERT INTO dbo.OrderItems
    (Id, OrderId, ProductId, TenantId, ProductName, Quantity, UnitPrice, Variant)
VALUES
    (NEWID(), @OrderBeta1, 'B1000000-0000-0000-0000-000000000002', @TenantBeta, N'Jean Slim Azul',     1, 82999.00, N'Talle 30'),
    (NEWID(), @OrderBeta1, 'B1000000-0000-0000-0000-000000000004', @TenantBeta, N'Campera Denim Black', 1, 119999.00, N'Talle M');

INSERT INTO dbo.Reservations
    (Id, TenantId, ConversationId, CustomerName, CustomerPhone, PartySize,
     ReservationTime, Status, Notes, ConfirmationToken, CreatedAt)
VALUES
    (NEWID(), @TenantBeta, @ConvBeta1, N'Maria Lopez', N'+549116661111', 3,
     DATEADD(day, 2, SYSDATETIMEOFFSET()), 0, N'Prueba tenant beta', N'BETARES001', SYSDATETIMEOFFSET());

/* ============================================================
   5. GAMMA OUTDOOR (TenantGamma) — datos con RLS activo
   ============================================================ */
EXEC sp_set_session_context @key = N'TenantId', @value = @TenantGamma, @read_only = 0;

DELETE FROM dbo.BusinessConfigs         WHERE TenantId = @TenantGamma;
DELETE FROM dbo.OrderItems              WHERE TenantId = @TenantGamma;
DELETE FROM dbo.Orders                  WHERE TenantId = @TenantGamma;
DELETE FROM dbo.Reservations            WHERE TenantId = @TenantGamma;
DELETE FROM dbo.ConversationMessages    WHERE TenantId = @TenantGamma;
DELETE FROM dbo.Conversations           WHERE TenantId = @TenantGamma;
DELETE FROM dbo.Products                WHERE TenantId = @TenantGamma;

-- BusinessConfig Gamma
INSERT INTO dbo.BusinessConfigs
    (Id, TenantId, OpeningHours, Branches, ShippingMethods, ReturnPolicy,
     WelcomeMessage, FallbackMessage, MaxRetryBeforeHandoff, UpdatedAt)
VALUES
    (NEWID(), @TenantGamma,
     N'Lun-Dom 08:00-22:00',
     N'Rosario|Cordoba|Mendoza',
     N'Expreso|Retiro|Flete',
     N'10 dias en condicion original',
     N'Hola, soy Guia Outdoor. Te ayudo a equiparte para aventura.',
     N'Contame actividad, clima o duracion del viaje.',
     3, SYSDATETIMEOFFSET());

-- Products Gamma (3 articulos outdoor)
INSERT INTO dbo.Products
    (Id, TenantId, Name, Description, Brand, Category, Tags, Price, StockQuantity, Sku, Embedding, CreatedAt, UpdatedAt)
VALUES
    ('C1000000-0000-0000-0000-000000000001', @TenantGamma,
     N'Mochila Trek 35L', N'Mochila outdoor de travesia con soporte lumbar.',
     N'Deuter', N'Outdoor', N'mochila,trekking,35l,travesia', 145999.00, 9, N'GAMMA-MOC-001', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('C1000000-0000-0000-0000-000000000002', @TenantGamma,
     N'Linterna Headlamp', N'Linterna frontal recargable 300 lumens para trail y camping.',
     N'Petzl', N'Outdoor', N'linterna,headlamp,trail,camping', 55999.00, 21, N'GAMMA-LIN-002', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
    ('C1000000-0000-0000-0000-000000000003', @TenantGamma,
     N'Campera Pluma Alpine', N'Campera termica alta montaña con pluma 700 fill.',
     N'The North Face', N'Outdoor', N'campera,pluma,alpine,montana', 249999.00, 3, N'GAMMA-CAM-003', NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

-- Conversations Gamma
DECLARE @ConvGamma1  UNIQUEIDENTIFIER = 'C2000000-0000-0000-0000-000000000001';
DECLARE @OrderGamma1 UNIQUEIDENTIFIER = 'C3000000-0000-0000-0000-000000000001';

INSERT INTO dbo.Conversations
    (Id, TenantId, ChannelUserId, Channel, Status, FailedClassificationCount, CreatedAt, LastActivityAt)
VALUES
    (@ConvGamma1, @TenantGamma, N'whatsapp:+549117771111', N'whatsapp', 0, 0,
     DATEADD(day, -5, SYSDATETIMEOFFSET()), DATEADD(minute, -10, SYSDATETIMEOFFSET()));

INSERT INTO dbo.ConversationMessages
    (Id, ConversationId, TenantId, Role, Content, AgentName, CreatedAt)
VALUES
    (NEWID(), @ConvGamma1, @TenantGamma, 0, N'Necesito una mochila para trekking.',  N'RouterPlugin',    DATEADD(day, -5, SYSDATETIMEOFFSET())),
    (NEWID(), @ConvGamma1, @TenantGamma, 1, N'Tengo la Trek 35L disponible.',        N'InventoryPlugin', DATEADD(day, -5, SYSDATETIMEOFFSET()));

INSERT INTO dbo.Orders
    (Id, TenantId, ConversationId, CustomerPhone, CustomerName, Status, TotalAmount,
     ShippingAddress, Notes, CreatedAt, UpdatedAt)
VALUES
    (@OrderGamma1, @TenantGamma, @ConvGamma1, N'+549117771111', N'Lucas Pereyra', 0, 145999.00,
     N'Av. Libertad 700', N'Prefiere retiro en tienda', DATEADD(day, -4, SYSDATETIMEOFFSET()), DATEADD(day, -4, SYSDATETIMEOFFSET()));

INSERT INTO dbo.OrderItems
    (Id, OrderId, ProductId, TenantId, ProductName, Quantity, UnitPrice, Variant)
VALUES
    (NEWID(), @OrderGamma1, 'C1000000-0000-0000-0000-000000000001', @TenantGamma, N'Mochila Trek 35L', 1, 145999.00, N'Color grafito');

INSERT INTO dbo.Reservations
    (Id, TenantId, ConversationId, CustomerName, CustomerPhone, PartySize,
     ReservationTime, Status, Notes, ConfirmationToken, CreatedAt)
VALUES
    (NEWID(), @TenantGamma, @ConvGamma1, N'Lucas Pereyra', N'+549117771111', 2,
     DATEADD(day, 3, SYSDATETIMEOFFSET()), 1, N'Prueba tenant gamma', N'GAMMARES001', SYSDATETIMEOFFSET());

/* ============================================================
   6. Reset contexto de sesion
   ============================================================ */
EXEC sp_set_session_context @key = N'TenantId', @value = NULL, @read_only = 0;

/* ============================================================
   Verificacion final de conteos
   ============================================================ */
SELECT
    t.name                  AS Tabla,
    SUM(p.rows)             AS Filas
FROM sys.tables t
JOIN sys.partitions p ON t.object_id = p.object_id AND p.index_id IN (0, 1)
WHERE t.schema_id = SCHEMA_ID('dbo')
GROUP BY t.name
ORDER BY t.name;

PRINT '';
PRINT '==> VadiSuite Master Seed completado exitosamente.';
PRINT '    Tenants: 3  |  AppUsers: 4  |  Products: 61 (54 Alfa + 4 Beta + 3 Gamma)';
PRINT '    Conversations: 4  |  Orders: 4  |  Reservations: 3';
PRINT '    Password de todos los usuarios: 123456';
