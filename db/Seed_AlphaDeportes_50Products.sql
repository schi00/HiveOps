SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @TenantAlpha UNIQUEIDENTIFIER = '11111111-1111-1111-1111-111111111111';

EXEC sp_set_session_context @key = N'TenantId', @value = @TenantAlpha, @read_only = 0;

MERGE dbo.Products AS target
USING (VALUES
    ('10000000-0000-0000-0000-000000000001', @TenantAlpha, N'Zapatillas Nike Pegasus 41', N'Calzado de running liviano con amortiguacion reactiva.', N'Nike', N'Calzado Running', N'nike,running,zapatillas,pegasus', CAST(134990.00 AS decimal(18,2)), 14, N'ALFA-NK-001'),
    ('10000000-0000-0000-0000-000000000002', @TenantAlpha, N'Zapatillas Adidas Supernova Rise', N'Zapatillas para entrenamiento diario con buena estabilidad.', N'Adidas', N'Calzado Running', N'adidas,running,supernova', CAST(129990.00 AS decimal(18,2)), 11, N'ALFA-AD-002'),
    ('10000000-0000-0000-0000-000000000003', @TenantAlpha, N'Zapatillas Puma Velocity Nitro 3', N'Modelo de running con espuma Nitro para distancias medias.', N'Puma', N'Calzado Running', N'puma,running,velocity', CAST(124990.00 AS decimal(18,2)), 9, N'ALFA-PM-003'),
    ('10000000-0000-0000-0000-000000000004', @TenantAlpha, N'Botines Adidas Predator League', N'Botines para cesped sintetico con control optimizado.', N'Adidas', N'Futbol', N'futbol,botines,adidas,predator', CAST(109990.00 AS decimal(18,2)), 18, N'ALFA-AD-004'),
    ('10000000-0000-0000-0000-000000000005', @TenantAlpha, N'Botines Nike Tiempo Legend 10', N'Botines de futbol con capellada suave y gran ajuste.', N'Nike', N'Futbol', N'futbol,botines,nike,tiempo', CAST(119990.00 AS decimal(18,2)), 15, N'ALFA-NK-005'),
    ('10000000-0000-0000-0000-000000000006', @TenantAlpha, N'Pelota Adidas Al Rihla Training', N'Pelota de entrenamiento resistente para cesped y sintetico.', N'Adidas', N'Futbol', N'pelota,futbol,adidas', CAST(32990.00 AS decimal(18,2)), 27, N'ALFA-AD-006'),
    ('10000000-0000-0000-0000-000000000007', @TenantAlpha, N'Pelota Nike Academy Team', N'Pelota de futbol cosida a maquina para uso frecuente.', N'Nike', N'Futbol', N'pelota,futbol,nike', CAST(29990.00 AS decimal(18,2)), 24, N'ALFA-NK-007'),
    ('10000000-0000-0000-0000-000000000008', @TenantAlpha, N'Remera Adidas Own The Run', N'Remera tecnica respirable para running y gimnasio.', N'Adidas', N'Ropa Deportiva', N'remera,adidas,running', CAST(24990.00 AS decimal(18,2)), 34, N'ALFA-AD-008'),
    ('10000000-0000-0000-0000-000000000009', @TenantAlpha, N'Remera Nike Miler Dri-FIT', N'Remera de secado rapido para entrenamientos intensos.', N'Nike', N'Ropa Deportiva', N'remera,nike,dri-fit', CAST(25990.00 AS decimal(18,2)), 31, N'ALFA-NK-009'),
    ('10000000-0000-0000-0000-000000000010', @TenantAlpha, N'Remera Puma Train Favorite', N'Remera liviana para gimnasio y funcional.', N'Puma', N'Ropa Deportiva', N'remera,puma,training', CAST(21990.00 AS decimal(18,2)), 29, N'ALFA-PM-010'),
    ('10000000-0000-0000-0000-000000000011', @TenantAlpha, N'Short Adidas Aeroready', N'Short deportivo con cintura elastica y tejido liviano.', N'Adidas', N'Ropa Deportiva', N'short,adidas,aeroready', CAST(21990.00 AS decimal(18,2)), 26, N'ALFA-AD-011'),
    ('10000000-0000-0000-0000-000000000012', @TenantAlpha, N'Short Nike Challenger 7', N'Short de running con slip interno y bolsillos laterales.', N'Nike', N'Ropa Deportiva', N'short,nike,running', CAST(23990.00 AS decimal(18,2)), 22, N'ALFA-NK-012'),
    ('10000000-0000-0000-0000-000000000013', @TenantAlpha, N'Short Under Armour Launch', N'Short elastico para running y entrenamiento.', N'Under Armour', N'Ropa Deportiva', N'short,under armour,launch', CAST(26990.00 AS decimal(18,2)), 20, N'ALFA-UA-013'),
    ('10000000-0000-0000-0000-000000000014', @TenantAlpha, N'Campera Rompeviento Nike Windrunner', N'Campera liviana para correr en dias ventosos.', N'Nike', N'Abrigos', N'campera,nike,windrunner', CAST(89990.00 AS decimal(18,2)), 13, N'ALFA-NK-014'),
    ('10000000-0000-0000-0000-000000000015', @TenantAlpha, N'Campera Adidas Tiro 24', N'Campera deportiva para entrenamiento y uso urbano.', N'Adidas', N'Abrigos', N'campera,adidas,tiro', CAST(84990.00 AS decimal(18,2)), 12, N'ALFA-AD-015'),
    ('10000000-0000-0000-0000-000000000016', @TenantAlpha, N'Buzo Puma Essentials Logo', N'Buzo frizado de uso diario con capucha.', N'Puma', N'Abrigos', N'buzo,puma,hoodie', CAST(55990.00 AS decimal(18,2)), 17, N'ALFA-PM-016'),
    ('10000000-0000-0000-0000-000000000017', @TenantAlpha, N'Buzo Under Armour Rival Fleece', N'Buzo comodo para entrada en calor y post entrenamiento.', N'Under Armour', N'Abrigos', N'buzo,under armour,fleece', CAST(61990.00 AS decimal(18,2)), 16, N'ALFA-UA-017'),
    ('10000000-0000-0000-0000-000000000018', @TenantAlpha, N'Calza Adidas Techfit Mujer', N'Calza de compresion para training y running.', N'Adidas', N'Ropa Deportiva', N'calza,adidas,techfit', CAST(34990.00 AS decimal(18,2)), 21, N'ALFA-AD-018'),
    ('10000000-0000-0000-0000-000000000019', @TenantAlpha, N'Calza Nike One Mujer', N'Calza elastica de tiro alto para gimnasio.', N'Nike', N'Ropa Deportiva', N'calza,nike,mujer', CAST(38990.00 AS decimal(18,2)), 19, N'ALFA-NK-019'),
    ('10000000-0000-0000-0000-000000000020', @TenantAlpha, N'Calza Puma Studio Ultrabare', N'Calza suave y flexible para yoga y entrenamiento.', N'Puma', N'Ropa Deportiva', N'calza,puma,yoga', CAST(35990.00 AS decimal(18,2)), 14, N'ALFA-PM-020'),
    ('10000000-0000-0000-0000-000000000021', @TenantAlpha, N'Mochila Adidas Classic Badge', N'Mochila urbana con compartimento principal amplio.', N'Adidas', N'Accesorios', N'mochila,adidas,urbana', CAST(32990.00 AS decimal(18,2)), 23, N'ALFA-AD-021'),
    ('10000000-0000-0000-0000-000000000022', @TenantAlpha, N'Mochila Nike Brasilia 9.5', N'Mochila resistente para entrenamiento y estudio.', N'Nike', N'Accesorios', N'mochila,nike,brasilia', CAST(37990.00 AS decimal(18,2)), 20, N'ALFA-NK-022'),
    ('10000000-0000-0000-0000-000000000023', @TenantAlpha, N'Bolso Puma Fundamentals Sports', N'Bolso mediano para gimnasio con correa regulable.', N'Puma', N'Accesorios', N'bolso,puma,gym', CAST(34990.00 AS decimal(18,2)), 18, N'ALFA-PM-023'),
    ('10000000-0000-0000-0000-000000000024', @TenantAlpha, N'Riñonera Under Armour Loudon', N'Riñonera compacta para llevar objetos esenciales.', N'Under Armour', N'Accesorios', N'rinonera,under armour', CAST(21990.00 AS decimal(18,2)), 25, N'ALFA-UA-024'),
    ('10000000-0000-0000-0000-000000000025', @TenantAlpha, N'Guantes de Gimnasio Adidas Essential', N'Guantes con palma reforzada para musculacion.', N'Adidas', N'Equipamiento Gym', N'guantes,gym,adidas', CAST(18990.00 AS decimal(18,2)), 28, N'ALFA-AD-025'),
    ('10000000-0000-0000-0000-000000000026', @TenantAlpha, N'Guantes de Gimnasio Nike Extreme', N'Guantes transpirables para pesas y entrenamiento funcional.', N'Nike', N'Equipamiento Gym', N'guantes,gym,nike', CAST(20990.00 AS decimal(18,2)), 24, N'ALFA-NK-026'),
    ('10000000-0000-0000-0000-000000000027', @TenantAlpha, N'Colchoneta Fitness Domyos Comfort', N'Colchoneta de 10 mm para yoga y abdominales.', N'Domyos', N'Equipamiento Gym', N'colchoneta,yoga,fitness', CAST(27990.00 AS decimal(18,2)), 18, N'ALFA-DM-027'),
    ('10000000-0000-0000-0000-000000000028', @TenantAlpha, N'Bandas Elasticas Set Training', N'Set de bandas de resistencia para entrenamiento en casa.', N'Everlast', N'Equipamiento Gym', N'bandas,resistencia,training', CAST(19990.00 AS decimal(18,2)), 32, N'ALFA-EV-028'),
    ('10000000-0000-0000-0000-000000000029', @TenantAlpha, N'Mancuerna Hexagonal 10 kg', N'Mancuerna engomada para entrenamiento funcional.', N'Body Sculpture', N'Equipamiento Gym', N'mancuerna,pesas,gym', CAST(28990.00 AS decimal(18,2)), 10, N'ALFA-BS-029'),
    ('10000000-0000-0000-0000-000000000030', @TenantAlpha, N'Mancuerna Hexagonal 15 kg', N'Mancuerna engomada para fuerza y musculacion.', N'Body Sculpture', N'Equipamiento Gym', N'mancuerna,pesas,fuerza', CAST(39990.00 AS decimal(18,2)), 8, N'ALFA-BS-030'),
    ('10000000-0000-0000-0000-000000000031', @TenantAlpha, N'Rodillera Nike Pro Open Patella', N'Rodillera de soporte para entrenamiento y running.', N'Nike', N'Accesorios', N'rodillera,nike,soporte', CAST(24990.00 AS decimal(18,2)), 14, N'ALFA-NK-031'),
    ('10000000-0000-0000-0000-000000000032', @TenantAlpha, N'Tobillera Adidas Support', N'Tobillera elastica para estabilizacion ligera.', N'Adidas', N'Accesorios', N'tobillera,adidas,soporte', CAST(17990.00 AS decimal(18,2)), 16, N'ALFA-AD-032'),
    ('10000000-0000-0000-0000-000000000033', @TenantAlpha, N'Media Nike Everyday Cushioned x3', N'Pack de medias deportivas acolchadas.', N'Nike', N'Accesorios', N'medias,nike,pack', CAST(14990.00 AS decimal(18,2)), 38, N'ALFA-NK-033'),
    ('10000000-0000-0000-0000-000000000034', @TenantAlpha, N'Media Adidas Cushioned Crew x3', N'Pack de medias altas para training y uso diario.', N'Adidas', N'Accesorios', N'medias,adidas,pack', CAST(13990.00 AS decimal(18,2)), 36, N'ALFA-AD-034'),
    ('10000000-0000-0000-0000-000000000035', @TenantAlpha, N'Gorra Puma Metal Cat', N'Gorra regulable para running y uso casual.', N'Puma', N'Accesorios', N'gorra,puma,training', CAST(16990.00 AS decimal(18,2)), 22, N'ALFA-PM-035'),
    ('10000000-0000-0000-0000-000000000036', @TenantAlpha, N'Gorra Under Armour Blitzing', N'Gorra elastica de secado rapido.', N'Under Armour', N'Accesorios', N'gorra,under armour', CAST(19990.00 AS decimal(18,2)), 19, N'ALFA-UA-036'),
    ('10000000-0000-0000-0000-000000000037', @TenantAlpha, N'Botella Adidas Steel 750 ml', N'Botella termica para gimnasio y aire libre.', N'Adidas', N'Accesorios', N'botella,adidas,hidratacion', CAST(15990.00 AS decimal(18,2)), 30, N'ALFA-AD-037'),
    ('10000000-0000-0000-0000-000000000038', @TenantAlpha, N'Botella Nike Hypercharge 950 ml', N'Botella deportiva con tapa a rosca.', N'Nike', N'Accesorios', N'botella,nike,hidratacion', CAST(16990.00 AS decimal(18,2)), 28, N'ALFA-NK-038'),
    ('10000000-0000-0000-0000-000000000039', @TenantAlpha, N'Auriculares JBL Endurance Run 2', N'Auriculares in-ear deportivos resistentes al sudor.', N'JBL', N'Tecnologia Deportiva', N'auriculares,jbl,deporte', CAST(45990.00 AS decimal(18,2)), 12, N'ALFA-JB-039'),
    ('10000000-0000-0000-0000-000000000040', @TenantAlpha, N'Smartwatch Garmin Forerunner 55', N'Reloj GPS para running con metricas basicas.', N'Garmin', N'Tecnologia Deportiva', N'smartwatch,garmin,running', CAST(249990.00 AS decimal(18,2)), 5, N'ALFA-GR-040'),
    ('10000000-0000-0000-0000-000000000041', @TenantAlpha, N'Pechera de Entrenamiento Set x10', N'Juego de pecheras para practicas de futbol.', N'Penalty', N'Futbol', N'pechera,futbol,equipo', CAST(42990.00 AS decimal(18,2)), 7, N'ALFA-PN-041'),
    ('10000000-0000-0000-0000-000000000042', @TenantAlpha, N'Conos de Entrenamiento Set x20', N'Conos flexibles para ejercicios de velocidad.', N'DRB', N'Futbol', N'conos,entrenamiento,futbol', CAST(24990.00 AS decimal(18,2)), 11, N'ALFA-DR-042'),
    ('10000000-0000-0000-0000-000000000043', @TenantAlpha, N'Raqueta Wilson Pro Staff Team', N'Raqueta liviana para jugadores intermedios.', N'Wilson', N'Tenis', N'tenis,raqueta,wilson', CAST(169990.00 AS decimal(18,2)), 6, N'ALFA-WL-043'),
    ('10000000-0000-0000-0000-000000000044', @TenantAlpha, N'Pelotas Wilson Championship x3', N'Tubo de pelotas de tenis para entrenamiento.', N'Wilson', N'Tenis', N'tenis,pelotas,wilson', CAST(14990.00 AS decimal(18,2)), 21, N'ALFA-WL-044'),
    ('10000000-0000-0000-0000-000000000045', @TenantAlpha, N'Raqueta Head Spark Elite', N'Raqueta para principiantes con buen control.', N'Head', N'Tenis', N'tenis,raqueta,head', CAST(119990.00 AS decimal(18,2)), 7, N'ALFA-HD-045'),
    ('10000000-0000-0000-0000-000000000046', @TenantAlpha, N'Bicicleta Fija Randers ARG-134', N'Bicicleta fija magnetica para cardio en casa.', N'Randers', N'Equipamiento Cardio', N'bicicleta,cardio,fitness', CAST(459990.00 AS decimal(18,2)), 4, N'ALFA-RD-046'),
    ('10000000-0000-0000-0000-000000000047', @TenantAlpha, N'Soga de Saltar Everlast Speed', N'Soga regulable para boxeo y entrenamiento funcional.', N'Everlast', N'Equipamiento Cardio', N'soga,saltar,cardio', CAST(12990.00 AS decimal(18,2)), 29, N'ALFA-EV-047'),
    ('10000000-0000-0000-0000-000000000048', @TenantAlpha, N'Guantes de Boxeo Everlast 12 oz', N'Guantes para bolsa y entrenamiento tecnico.', N'Everlast', N'Boxeo', N'boxeo,guantes,everlast', CAST(49990.00 AS decimal(18,2)), 13, N'ALFA-EV-048'),
    ('10000000-0000-0000-0000-000000000049', @TenantAlpha, N'Protector Bucal Shock Doctor', N'Protector bucal para deportes de contacto.', N'Shock Doctor', N'Boxeo', N'boxeo,protector,bucal', CAST(9990.00 AS decimal(18,2)), 35, N'ALFA-SD-049'),
    ('10000000-0000-0000-0000-000000000050', @TenantAlpha, N'Bolsa de Arena Everlast 25 kg', N'Bolsa de boxeo para entrenamiento de potencia.', N'Everlast', N'Boxeo', N'boxeo,bolsa,entrenamiento', CAST(139990.00 AS decimal(18,2)), 5, N'ALFA-EV-050')
) AS src (Id, TenantId, Name, Description, Brand, Category, Tags, Price, StockQuantity, Sku)
ON target.TenantId = src.TenantId AND target.Sku = src.Sku
WHEN MATCHED THEN
    UPDATE SET
        target.Name = src.Name,
        target.Description = src.Description,
        target.Brand = src.Brand,
        target.Category = src.Category,
        target.Tags = src.Tags,
        target.Price = src.Price,
        target.StockQuantity = src.StockQuantity,
        target.UpdatedAt = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN
    INSERT (Id, TenantId, Name, Description, Brand, Category, Tags, Price, StockQuantity, Sku, Embedding, CreatedAt, UpdatedAt)
    VALUES (src.Id, src.TenantId, src.Name, src.Description, src.Brand, src.Category, src.Tags, src.Price, src.StockQuantity, src.Sku, NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

DELETE FROM dbo.Synonyms
WHERE TenantId = @TenantAlpha
    AND Term LIKE N'%' + NCHAR(195) + N'%';

MERGE dbo.Synonyms AS target
USING (VALUES
    -- General deportes
    (N'deporte', N'deportes'),
    (N'deportes', N'deportes'),
    (N'deportivo', N'deportes'),
    (N'deportivos', N'deportes'),
    (N'sport', N'deportes'),
    (N'sports', N'deportes'),
    (N'sporting', N'deportes'),
    (N'actividad fisica', N'deportes'),
    (N'entrenamiento', N'training'),
    (N'entreno', N'training'),
    (N'entrenar', N'training'),
    (N'training', N'training'),
    (N'workout', N'training'),
    (N'fit', N'fitness'),
    (N'fitness', N'fitness'),
    (N'wellness', N'fitness'),
    (N'cross training', N'crossfit'),
    (N'crossfit', N'crossfit'),

    -- Futbol
    (N'football', N'futbol'),
    (N'soccer', N'futbol'),
    (N'footbal', N'futbol'),
    (N'footbool', N'futbol'),
    (N'fulbo', N'futbol'),
    (N'fulbito', N'futbol'),
    (N'futbol', N'futbol'),
    (N'futbol 5', N'futbol'),
    (N'futbol cinco', N'futbol'),
    (N'futsal', N'futbol'),
    (N'balonpie', N'futbol'),
    (N'cancha', N'futbol'),
    (N'botin', N'botines'),
    (N'botines', N'botines'),
    (N'tapones', N'botines'),
    (N'chimpunes', N'botines'),
    (N'pelota futbol', N'pelota futbol'),
    (N'balon', N'pelota futbol'),
    (N'pelota', N'pelota futbol'),
    (N'arquero', N'guantes arquero'),
    (N'portero', N'guantes arquero'),
    (N'guantes portero', N'guantes arquero'),
    (N'guantes arquero', N'guantes arquero'),
    (N'espinillera', N'espinilleras'),
    (N'espinilleras', N'espinilleras'),
    (N'canillera', N'espinilleras'),
    (N'canilleras', N'espinilleras'),

    -- Running y atletismo
    (N'run', N'running'),
    (N'runner', N'running'),
    (N'runners', N'running'),
    (N'running', N'running'),
    (N'correr', N'running'),
    (N'corredor', N'running'),
    (N'corredora', N'running'),
    (N'atletismo', N'running'),
    (N'track', N'running'),
    (N'trail', N'trail running'),
    (N'trail running', N'trail running'),
    (N'trailrun', N'trail running'),
    (N'maraton', N'running'),
    (N'media maraton', N'running'),
    (N'jogging', N'running'),

    -- Tenis / padel / raqueta
    (N'tennis', N'tenis'),
    (N'tenis', N'tenis'),
    (N'tenis de campo', N'tenis'),
    (N'tennis court', N'tenis'),
    (N'raqueta', N'raquetas'),
    (N'raquetas', N'raquetas'),
    (N'racket', N'raquetas'),
    (N'pelota tenis', N'pelotas tenis'),
    (N'pelotas tenis', N'pelotas tenis'),
    (N'grip', N'grip raqueta'),
    (N'overgrip', N'grip raqueta'),
    (N'cordaje', N'encordado'),
    (N'encordado', N'encordado'),
    (N'padel', N'padel'),
    (N'paddle', N'padel'),
    (N'pala', N'pala padel'),
    (N'pala padel', N'pala padel'),
    (N'paleta padel', N'pala padel'),
    (N'pelota padel', N'pelotas padel'),
    (N'pelotas padel', N'pelotas padel'),
    (N'pickleball', N'pickleball'),

    -- Basquet
    (N'basquet', N'basquet'),
    (N'basquetbol', N'basquet'),
    (N'basket', N'basquet'),
    (N'basketball', N'basquet'),
    (N'nba', N'basquet'),
    (N'aro', N'aro basquet'),
    (N'aro basquet', N'aro basquet'),
    (N'pelota basquet', N'pelota basquet'),
    (N'balon basquet', N'pelota basquet'),
    (N'zapatillas basquet', N'calzado basquet'),

    -- Voley
    (N'voley', N'voleibol'),
    (N'voleibol', N'voleibol'),
    (N'volley', N'voleibol'),
    (N'volleyball', N'voleibol'),
    (N'pelota voley', N'pelota voleibol'),
    (N'balon voley', N'pelota voleibol'),
    (N'rodillera voley', N'rodilleras'),

    -- Handball / rugby / hockey
    (N'handball', N'handball'),
    (N'balonmano', N'handball'),
    (N'rugby', N'rugby'),
    (N'pelota rugby', N'pelota rugby'),
    (N'hockey', N'hockey'),
    (N'hockey cesped', N'hockey'),
    (N'hockey hielo', N'hockey'),
    (N'palos hockey', N'stick hockey'),
    (N'stick', N'stick hockey'),

    -- Natacion
    (N'natacion', N'natacion'),
    (N'nadar', N'natacion'),
    (N'swim', N'natacion'),
    (N'swimming', N'natacion'),
    (N'malla', N'traje de bano'),
    (N'traje de bano', N'traje de bano'),
    (N'gorra natacion', N'gorra natacion'),
    (N'antiparras', N'antiparras'),
    (N'lentes natacion', N'antiparras'),
    (N'patas rana', N'aletas'),
    (N'aletas', N'aletas'),
    (N'tabla natacion', N'tabla natacion'),
    (N'pull buoy', N'pull buoy'),

    -- Ciclismo
    (N'bici', N'bicicleta'),
    (N'bicicleta', N'bicicleta'),
    (N'bike', N'bicicleta'),
    (N'cycling', N'ciclismo'),
    (N'ciclismo', N'ciclismo'),
    (N'mtb', N'mountain bike'),
    (N'mountain bike', N'mountain bike'),
    (N'ruta', N'ciclismo ruta'),
    (N'ciclismo ruta', N'ciclismo ruta'),
    (N'casco bici', N'casco ciclismo'),
    (N'casco ciclismo', N'casco ciclismo'),
    (N'caramanola', N'botella deportiva'),
    (N'porta caramanola', N'porta botella'),
    (N'calza ciclismo', N'ropa ciclismo'),
    (N'maillot', N'jersey ciclismo'),
    (N'jersey ciclismo', N'jersey ciclismo'),

    -- Boxeo / combate
    (N'box', N'boxeo'),
    (N'boxing', N'boxeo'),
    (N'boxeo', N'boxeo'),
    (N'mma', N'combate'),
    (N'artes marciales', N'combate'),
    (N'muay thai', N'combate'),
    (N'kickboxing', N'combate'),
    (N'guantes box', N'guantes boxeo'),
    (N'guantes boxeo', N'guantes boxeo'),
    (N'vendas', N'vendas boxeo'),
    (N'vendas boxeo', N'vendas boxeo'),
    (N'protector bucal', N'protector bucal'),
    (N'bucal', N'protector bucal'),
    (N'cabezal', N'protector cabeza'),
    (N'coquilla', N'protector inguinal'),
    (N'bolsa boxeo', N'bolsa de boxeo'),
    (N'bolsa de boxeo', N'bolsa de boxeo'),
    (N'pera loca', N'pera boxeo'),

    -- Gym / musculacion / funcional
    (N'gimnasio', N'gym'),
    (N'gim', N'gym'),
    (N'gym', N'gym'),
    (N'musculacion', N'fuerza'),
    (N'fuerza', N'fuerza'),
    (N'pesas', N'pesas'),
    (N'weightlifting', N'pesas'),
    (N'halterofilia', N'pesas'),
    (N'mancuerna', N'mancuernas'),
    (N'mancuernas', N'mancuernas'),
    (N'dumbbell', N'mancuernas'),
    (N'barra', N'barra olimpica'),
    (N'barra olimpica', N'barra olimpica'),
    (N'disco', N'discos olimpicos'),
    (N'discos', N'discos olimpicos'),
    (N'pesa rusa', N'kettlebell'),
    (N'kettlebell', N'kettlebell'),
    (N'tobillera con peso', N'lastre'),
    (N'lastre', N'lastre'),
    (N'banco plano', N'banco gimnasio'),
    (N'banco gimnasio', N'banco gimnasio'),
    (N'jaula', N'rack de fuerza'),
    (N'rack', N'rack de fuerza'),
    (N'rack de fuerza', N'rack de fuerza'),
    (N'power rack', N'rack de fuerza'),
    (N'smith', N'maquina smith'),
    (N'maquina smith', N'maquina smith'),
    (N'polea', N'maquina polea'),
    (N'maquina polea', N'maquina polea'),
    (N'bandas', N'bandas elasticas'),
    (N'bandas elasticas', N'bandas elasticas'),
    (N'resistance bands', N'bandas elasticas'),
    (N'soga', N'soga de saltar'),
    (N'soga de saltar', N'soga de saltar'),
    (N'comba', N'soga de saltar'),
    (N'colchoneta', N'colchoneta fitness'),
    (N'mat', N'colchoneta fitness'),
    (N'foam roller', N'rodillo masaje'),
    (N'rodillo', N'rodillo masaje'),
    (N'rodillo masaje', N'rodillo masaje'),

    -- Cardio y maquinas
    (N'cardio', N'cardio'),
    (N'cinta', N'cinta de correr'),
    (N'cinta de correr', N'cinta de correr'),
    (N'trotadora', N'cinta de correr'),
    (N'eliptico', N'eliptica'),
    (N'eliptica', N'eliptica'),
    (N'bici fija', N'bicicleta fija'),
    (N'bicicleta fija', N'bicicleta fija'),
    (N'spinning', N'bicicleta fija'),
    (N'remo', N'maquina de remo'),
    (N'maquina de remo', N'maquina de remo'),
    (N'escalador', N'stepper'),
    (N'step', N'stepper'),
    (N'stepper', N'stepper'),

    -- Yoga / pilates / movilidad
    (N'yoga', N'yoga'),
    (N'pilates', N'pilates'),
    (N'estiramiento', N'movilidad'),
    (N'movilidad', N'movilidad'),
    (N'bloque yoga', N'bloque yoga'),
    (N'bloques yoga', N'bloque yoga'),
    (N'cinta yoga', N'correa yoga'),
    (N'correa yoga', N'correa yoga'),
    (N'pelota pilates', N'pelota pilates'),
    (N'fitball', N'pelota pilates'),
    (N'bosu', N'bosu'),

    -- Trekking / outdoor / camping
    (N'trekking', N'trekking'),
    (N'hiking', N'trekking'),
    (N'senderismo', N'trekking'),
    (N'montanismo', N'trekking'),
    (N'outdoor', N'outdoor'),
    (N'camping', N'camping'),
    (N'carpa', N'carpa camping'),
    (N'tienda', N'carpa camping'),
    (N'mochila trekking', N'mochila trekking'),
    (N'baston trekking', N'bastones trekking'),
    (N'bastones trekking', N'bastones trekking'),
    (N'bolsa dormir', N'bolsa de dormir'),
    (N'bolsa de dormir', N'bolsa de dormir'),
    (N'linterna frontal', N'frontal'),
    (N'frontal', N'frontal'),

    -- Calzado
    (N'zapa', N'calzado'),
    (N'zapas', N'calzado'),
    (N'zapatilla', N'zapatillas'),
    (N'zapatillas', N'zapatillas'),
    (N'sneaker', N'zapatillas'),
    (N'sneakers', N'zapatillas'),
    (N'tenis shoes', N'zapatillas'),
    (N'calzado', N'calzado'),
    (N'calzado deportivo', N'calzado'),
    (N'calzado running', N'calzado running'),
    (N'calzado futbol', N'calzado futbol'),
    (N'bota trekking', N'botas trekking'),
    (N'botas trekking', N'botas trekking'),
    (N'ojota', N'sandalias'),
    (N'ojotas', N'sandalias'),
    (N'sandalia', N'sandalias'),
    (N'sandalias', N'sandalias'),

    -- Ropa deportiva
    (N'ropa', N'ropa deportiva'),
    (N'ropa deportiva', N'ropa deportiva'),
    (N'indumentaria', N'ropa deportiva'),
    (N'indumentaria deportiva', N'ropa deportiva'),
    (N'outfit deportivo', N'ropa deportiva'),
    (N'remera', N'remeras deportivas'),
    (N'remeras', N'remeras deportivas'),
    (N'tshirt', N'remeras deportivas'),
    (N't-shirt', N'remeras deportivas'),
    (N'musculosa', N'tanque deportivo'),
    (N'tanque', N'tanque deportivo'),
    (N'short', N'shorts deportivos'),
    (N'shorts', N'shorts deportivos'),
    (N'calza', N'calzas deportivas'),
    (N'calzas', N'calzas deportivas'),
    (N'legging', N'calzas deportivas'),
    (N'leggings', N'calzas deportivas'),
    (N'pantalon jogging', N'pantalon deportivo'),
    (N'jogger', N'pantalon deportivo'),
    (N'pantalon deportivo', N'pantalon deportivo'),
    (N'pantalon entrenamiento', N'pantalon deportivo'),
    (N'buzo', N'buzo deportivo'),
    (N'hoodie', N'buzo deportivo'),
    (N'campera', N'campera deportiva'),
    (N'campera rompeviento', N'rompeviento'),
    (N'rompeviento', N'rompeviento'),
    (N'chaleco', N'chaleco deportivo'),
    (N'camiseta', N'camiseta deportiva'),
    (N'jersey', N'camiseta deportiva'),
    (N'medias', N'medias deportivas'),
    (N'medias compresion', N'medias de compresion'),
    (N'compresion', N'ropa compresion'),
    (N'ropa compresion', N'ropa compresion'),

    -- Accesorios deportivos
    (N'accesorio', N'accesorios deportivos'),
    (N'accesorios', N'accesorios deportivos'),
    (N'accesorio deportivo', N'accesorios deportivos'),
    (N'equipamiento', N'articulos deportivos'),
    (N'articulo', N'articulos deportivos'),
    (N'articulos', N'articulos deportivos'),
    (N'articulo deportivo', N'articulos deportivos'),
    (N'articulos deportivos', N'articulos deportivos'),
    (N'mochila', N'mochilas deportivas'),
    (N'backpack', N'mochilas deportivas'),
    (N'bolso', N'bolsos deportivos'),
    (N'bolso deportivo', N'bolsos deportivos'),
    (N'duffle', N'bolsos deportivos'),
    (N'rinonera', N'rinoneras'),
    (N'cangurera', N'rinoneras'),
    (N'gorra', N'gorras deportivas'),
    (N'cap', N'gorras deportivas'),
    (N'visera', N'viseras deportivas'),
    (N'botella', N'botella deportiva'),
    (N'botella deportiva', N'botella deportiva'),
    (N'hidratacion', N'hidratacion'),
    (N'termo', N'botella termica'),
    (N'guante', N'guantes deportivos'),
    (N'guantes', N'guantes deportivos'),
    (N'rodillera', N'rodilleras'),
    (N'rodilleras', N'rodilleras'),
    (N'tobillera', N'tobilleras'),
    (N'tobilleras', N'tobilleras'),
    (N'munequera', N'munequeras'),
    (N'munequeras', N'munequeras'),
    (N'codera', N'coderas'),
    (N'coderas', N'coderas'),
    (N'protector', N'protecciones deportivas'),
    (N'protecciones', N'protecciones deportivas'),

    -- Tecnologia deportiva
    (N'tecnologia deportiva', N'tecnologia deportiva'),
    (N'wearable', N'wearables deportivos'),
    (N'wearables', N'wearables deportivos'),
    (N'smartwatch', N'reloj deportivo'),
    (N'reloj gps', N'reloj deportivo'),
    (N'reloj deportivo', N'reloj deportivo'),
    (N'hrm', N'frecuencia cardiaca'),
    (N'cardiofrecuencimetro', N'frecuencia cardiaca'),
    (N'banda cardiaca', N'frecuencia cardiaca'),
    (N'auriculares', N'auriculares deportivos'),
    (N'auriculares deportivos', N'auriculares deportivos')
) AS src (Term, Normalized)
ON target.TenantId = @TenantAlpha AND target.Term = src.Term
WHEN MATCHED THEN
    UPDATE SET
        target.Normalized = src.Normalized,
        target.UpdatedAt = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN
    INSERT (Id, TenantId, Term, Normalized, CreatedAt, UpdatedAt)
    VALUES (NEWID(), @TenantAlpha, src.Term, src.Normalized, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())
WHEN NOT MATCHED BY SOURCE AND target.TenantId = @TenantAlpha THEN
    DELETE;

MERGE dbo.Concepts AS target
USING (VALUES
    (N'futbol', N'sport'),
    (N'tenis', N'sport'),
    (N'running', N'sport')
) AS src (Name, [Type])
ON target.TenantId = @TenantAlpha AND target.Name = src.Name
WHEN MATCHED THEN
    UPDATE SET
        target.[Type] = src.[Type],
        target.UpdatedAt = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN
    INSERT (Id, TenantId, Name, [Type], CreatedAt, UpdatedAt)
    VALUES (NEWID(), @TenantAlpha, src.Name, src.[Type], SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

MERGE dbo.ConceptProductMap AS target
USING (
    SELECT @TenantAlpha AS TenantId, c.Id AS ConceptId, src.Category, src.Tags, src.Priority
    FROM (VALUES
        (N'futbol', N'Futbol', N'botines,pelota', 10),
        (N'tenis', N'Tenis', N'raqueta,pelotas', 20),
        (N'running', N'Running', N'zapatillas,remera', 30)
    ) AS src (ConceptName, Category, Tags, Priority)
    INNER JOIN dbo.Concepts c
        ON c.TenantId = @TenantAlpha
       AND c.Name = src.ConceptName
) AS src
ON target.TenantId = src.TenantId
   AND target.ConceptId = src.ConceptId
   AND ISNULL(target.Category, N'') = ISNULL(src.Category, N'')
   AND ISNULL(target.Tags, N'') = ISNULL(src.Tags, N'')
WHEN MATCHED THEN
    UPDATE SET
        target.Priority = src.Priority,
        target.UpdatedAt = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN
    INSERT (Id, TenantId, ConceptId, Category, Tags, Priority, CreatedAt, UpdatedAt)
    VALUES (NEWID(), src.TenantId, src.ConceptId, src.Category, src.Tags, src.Priority, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

EXEC sp_set_session_context @key = N'TenantId', @value = @TenantAlpha, @read_only = 0;

SELECT COUNT(*) AS TotalProductsForAlpha
FROM dbo.Products
WHERE TenantId = @TenantAlpha;

SELECT TOP 10 Name, Brand, Category, Price, StockQuantity, Sku
FROM dbo.Products
WHERE TenantId = @TenantAlpha
ORDER BY Name;

SELECT Term, Normalized
FROM dbo.Synonyms
WHERE TenantId = @TenantAlpha
ORDER BY Term;

SELECT Name, [Type]
FROM dbo.Concepts
WHERE TenantId = @TenantAlpha
ORDER BY Name;