-- =====================================================
-- HiveOps Support Bot - Database Setup Script
-- Base de datos: Hive (sin ventas, solo soporte)
-- =====================================================

SET QUOTED_IDENTIFIER ON;
GO

USE master;
GO

IF DB_ID('Hive') IS NOT NULL
BEGIN
    ALTER DATABASE Hive SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE Hive;
END
GO

CREATE DATABASE Hive;
GO

USE Hive;
GO

-- =====================================================
-- TABLAS BASE
-- =====================================================

-- Tenants
CREATE TABLE Tenants (
    Id uniqueidentifier PRIMARY KEY DEFAULT NEWID(),
    Name nvarchar(200) NOT NULL,
    Industry nvarchar(100),
    WhatsAppNumber nvarchar(30),
    ApiKey nvarchar(128) NOT NULL,
    IsActive bit NOT NULL DEFAULT 1,
    ConfigJson nvarchar(max),
    CreatedAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET()
);

CREATE UNIQUE INDEX IX_Tenants_ApiKey ON Tenants(ApiKey);
CREATE UNIQUE INDEX IX_Tenants_WhatsAppNumber ON Tenants(WhatsAppNumber) WHERE WhatsAppNumber IS NOT NULL;

-- Usuarios de aplicacion
CREATE TABLE AppUsers (
    Id uniqueidentifier PRIMARY KEY DEFAULT NEWID(),
    Username nvarchar(100) NOT NULL,
    Email nvarchar(200) NOT NULL,
    PasswordHash nvarchar(max) NOT NULL,
    Role nvarchar(20) NOT NULL,
    TenantId uniqueidentifier,
    IsActive bit NOT NULL DEFAULT 1,
    PasswordResetTokenHash nvarchar(128),
    PasswordResetTokenExpiresAt datetimeoffset,
    CreatedAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    UpdatedAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET()
);

CREATE UNIQUE INDEX IX_AppUsers_Username ON AppUsers(Username);
CREATE UNIQUE INDEX IX_AppUsers_Email ON AppUsers(Email);
CREATE INDEX IX_AppUsers_Role_IsActive ON AppUsers(Role, IsActive);

ALTER TABLE AppUsers ADD CONSTRAINT FK_AppUsers_Tenants
    FOREIGN KEY (TenantId) REFERENCES Tenants(Id) ON DELETE SET NULL;

-- Conversaciones
CREATE TABLE Conversations (
    Id uniqueidentifier PRIMARY KEY DEFAULT NEWID(),
    TenantId uniqueidentifier NOT NULL,
    ChannelUserId nvarchar(100) NOT NULL,
    Channel nvarchar(50) NOT NULL,
    Status int NOT NULL DEFAULT 0, -- ConversationStatus: 0=Active, 1=Closed, 2=Escalated
    FailedClassificationCount int NOT NULL DEFAULT 0,
    CreatedAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    LastActivityAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET()
);

CREATE INDEX IX_Conversations_TenantId_ChannelUserId ON Conversations(TenantId, ChannelUserId);

ALTER TABLE Conversations ADD CONSTRAINT FK_Conversations_Tenants
    FOREIGN KEY (TenantId) REFERENCES Tenants(Id) ON DELETE NO ACTION;

-- Mensajes de conversacion
CREATE TABLE ConversationMessages (
    Id uniqueidentifier PRIMARY KEY DEFAULT NEWID(),
    ConversationId uniqueidentifier NOT NULL,
    TenantId uniqueidentifier NOT NULL,
    Role int NOT NULL, -- MessageRole: 0=User, 1=Assistant, 2=System
    Content nvarchar(max) NOT NULL,
    AgentName nvarchar(100),
    ExternalMessageId nvarchar(255),
    CreatedAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET()
);

CREATE INDEX IX_ConversationMessages_ConversationId_CreatedAt ON ConversationMessages(ConversationId, CreatedAt);
CREATE INDEX IX_ConversationMessages_TenantId_ExternalMessageId ON ConversationMessages(TenantId, ExternalMessageId);

ALTER TABLE ConversationMessages ADD CONSTRAINT FK_ConversationMessages_Conversations
    FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE NO ACTION;

ALTER TABLE ConversationMessages ADD CONSTRAINT FK_ConversationMessages_Tenants
    FOREIGN KEY (TenantId) REFERENCES Tenants(Id) ON DELETE NO ACTION;

-- Configuracion de negocio por tenant
CREATE TABLE BusinessConfigs (
    Id uniqueidentifier PRIMARY KEY DEFAULT NEWID(),
    TenantId uniqueidentifier NOT NULL,
    OpeningHours nvarchar(max),
    Branches nvarchar(max),
    ShippingMethods nvarchar(max),
    ReturnPolicy nvarchar(max),
    WelcomeMessage nvarchar(1000),
    FallbackMessage nvarchar(1000),
    MaxRetryBeforeHandoff int NOT NULL DEFAULT 2,
    UpdatedAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET()
);

CREATE UNIQUE INDEX IX_BusinessConfigs_TenantId ON BusinessConfigs(TenantId);

ALTER TABLE BusinessConfigs ADD CONSTRAINT FK_BusinessConfigs_Tenants
    FOREIGN KEY (TenantId) REFERENCES Tenants(Id) ON DELETE NO ACTION;

-- =====================================================
-- TABLAS DE INCIDENTES (Soporte Bot)
-- =====================================================

-- Incidentes
CREATE TABLE Incidents (
    Id uniqueidentifier PRIMARY KEY DEFAULT NEWID(),
    TenantId uniqueidentifier NOT NULL,
    ConversationId uniqueidentifier NOT NULL,
    Title nvarchar(500) NOT NULL,
    Description nvarchar(max) NOT NULL,
    Category int NOT NULL DEFAULT 3, -- IncidentCategory: 0=Code, 1=Database, 2=Infrastructure, 3=Other
    Severity int NOT NULL DEFAULT 0, -- IncidentSeverity: 0=Low, 1=Medium, 2=High, 3=Critical
    Status int NOT NULL DEFAULT 0, -- IncidentStatus: 0=Open, 1=InProgress, 2=PendingInfo, 3=Resolved, 4=Closed
    AssignedTo nvarchar(100) NOT NULL DEFAULT 'bot',
    ResolutionNotes nvarchar(max),
    GitBranch nvarchar(200),
    GitCommitHash nvarchar(100),
    DeployStatus nvarchar(50),
    CreatedAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    UpdatedAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    ResolvedAt datetimeoffset
);

CREATE INDEX IX_Incidents_TenantId ON Incidents(TenantId);
CREATE INDEX IX_Incidents_Status ON Incidents(Status);
CREATE INDEX IX_Incidents_CreatedAt ON Incidents(CreatedAt);

ALTER TABLE Incidents ADD CONSTRAINT FK_Incidents_Tenants
    FOREIGN KEY (TenantId) REFERENCES Tenants(Id) ON DELETE NO ACTION;

ALTER TABLE Incidents ADD CONSTRAINT FK_Incidents_Conversations
    FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE NO ACTION;

-- Adjuntos de incidente
CREATE TABLE IncidentAttachments (
    Id uniqueidentifier PRIMARY KEY DEFAULT NEWID(),
    TenantId uniqueidentifier NOT NULL,
    IncidentId uniqueidentifier NOT NULL,
    Type int NOT NULL DEFAULT 5, -- IncidentAttachmentType: 0=Log, 1=Screenshot, 2=StackTrace, 3=SqlScript, 4=Diff, 5=Other
    FileName nvarchar(500) NOT NULL,
    Content nvarchar(max) NOT NULL,
    CreatedAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET()
);

CREATE INDEX IX_IncidentAttachments_IncidentId ON IncidentAttachments(IncidentId);

ALTER TABLE IncidentAttachments ADD CONSTRAINT FK_IncidentAttachments_Incidents
    FOREIGN KEY (IncidentId) REFERENCES Incidents(Id) ON DELETE NO ACTION;

ALTER TABLE IncidentAttachments ADD CONSTRAINT FK_IncidentAttachments_Tenants
    FOREIGN KEY (TenantId) REFERENCES Tenants(Id) ON DELETE NO ACTION;

-- Logs de diagnostico
CREATE TABLE DiagnosticLogs (
    Id uniqueidentifier PRIMARY KEY DEFAULT NEWID(),
    TenantId uniqueidentifier NOT NULL,
    IncidentId uniqueidentifier NOT NULL,
    StepName nvarchar(200) NOT NULL,
    Result nvarchar(max) NOT NULL,
    IsSuccess bit NOT NULL DEFAULT 1,
    CreatedAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET()
);

CREATE INDEX IX_DiagnosticLogs_IncidentId ON DiagnosticLogs(IncidentId);
CREATE INDEX IX_DiagnosticLogs_CreatedAt ON DiagnosticLogs(CreatedAt);

ALTER TABLE DiagnosticLogs ADD CONSTRAINT FK_DiagnosticLogs_Incidents
    FOREIGN KEY (IncidentId) REFERENCES Incidents(Id) ON DELETE NO ACTION;

ALTER TABLE DiagnosticLogs ADD CONSTRAINT FK_DiagnosticLogs_Tenants
    FOREIGN KEY (TenantId) REFERENCES Tenants(Id) ON DELETE NO ACTION;

-- Articulos de Base de Conocimiento
CREATE TABLE KbArticles (
    Id uniqueidentifier PRIMARY KEY DEFAULT NEWID(),
    TenantId uniqueidentifier NOT NULL,
    SourceIncidentId uniqueidentifier,
    Title nvarchar(500) NOT NULL,
    Content nvarchar(max) NOT NULL,
    Category nvarchar(100) NOT NULL DEFAULT 'Other',
    Tags nvarchar(500) NOT NULL DEFAULT '',
    ResolutionSteps nvarchar(max) NOT NULL DEFAULT '',
    IsPublished bit NOT NULL DEFAULT 0,
    CreatedAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    UpdatedAt datetimeoffset NOT NULL DEFAULT SYSDATETIMEOFFSET()
);

CREATE INDEX IX_KbArticles_TenantId ON KbArticles(TenantId);
CREATE INDEX IX_KbArticles_Category ON KbArticles(Category);
CREATE INDEX IX_KbArticles_IsPublished ON KbArticles(IsPublished);

ALTER TABLE KbArticles ADD CONSTRAINT FK_KbArticles_Tenants
    FOREIGN KEY (TenantId) REFERENCES Tenants(Id) ON DELETE NO ACTION;

-- =====================================================
-- DATOS DE PRUEBA
-- =====================================================

-- Tenant de prueba: DeportesPrueba
DECLARE @TenantId uniqueidentifier = NEWID();
DECLARE @TenantApiKey nvarchar(128) = 'deportes-api-key-12345678';

INSERT INTO Tenants (Id, Name, Industry, WhatsAppNumber, ApiKey, IsActive, ConfigJson, CreatedAt)
VALUES (
    @TenantId,
    'DeportesPrueba',
    'deportes',
    '+5491150000000',
    @TenantApiKey,
    1,
    N'{"WelcomeMessage": "Hola! Soy el asistente de soporte de DeportesPrueba. Como puedo ayudarte?", "FallbackMessage": "Disculpa, no entendi tu mensaje. Puedes reformularlo o escribir hablar con persona para hablar con un agente.", "MaxRetryBeforeHandoff": 2, "AllowedIntents": ["Greeting", "HumanHandoff", "IncidentReport", "IncidentQuery", "IncidentApprove", "IncidentReject", "DeployRequest", "Unknown"]}',
    SYSDATETIMEOFFSET()
);

-- Usuario de prueba: deportes / 123456
-- Hash generado con PBKDF2 (100000 iteraciones, SHA256)
DECLARE @UserId uniqueidentifier = NEWID();

INSERT INTO AppUsers (Id, Username, Email, PasswordHash, Role, TenantId, IsActive, CreatedAt, UpdatedAt)
VALUES (
    @UserId,
    'deportes',
    'deportes@deportesprueba.local',
    'v1.100000.AAAAAAAAAAAAAAAAAAAAAAAAAEE=.BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=',
    'Tenant',
    @TenantId,
    1,
    SYSDATETIMEOFFSET(),
    SYSDATETIMEOFFSET()
);

-- Hash valido para contrasena "123456"
UPDATE AppUsers SET PasswordHash = 'v1.100000.Qv0McvSDZ3OhGqAAT8RvMQ==.xrC/WQLxtC0y5pjBM0c4SZ2S5BXCX0nXQJJmNt9MFGk=' WHERE Username = 'deportes';

-- Configuracion de negocio para el tenant
INSERT INTO BusinessConfigs (Id, TenantId, OpeningHours, Branches, WelcomeMessage, FallbackMessage, MaxRetryBeforeHandoff, UpdatedAt)
VALUES (
    NEWID(),
    @TenantId,
    '{"monday":"9:00-18:00","tuesday":"9:00-18:00","wednesday":"9:00-18:00","thursday":"9:00-18:00","friday":"9:00-17:00"}',
    '[{"name":"Casa Central","address":"Av. Principal 123","phone":"+5491112345678"}]',
    'Hola! Soy el asistente de soporte de DeportesPrueba. Como puedo ayudarte hoy?',
    'Disculpa, no entendi tu mensaje. Escribe "hablar con persona" para contactar un agente.',
    2,
    SYSDATETIMEOFFSET()
);

-- =====================================================
-- ESCENARIO DE PRUEBA: Incidente de error de datos
-- =====================================================

-- Crear una conversacion activa
DECLARE @ConversationId uniqueidentifier = NEWID();

INSERT INTO Conversations (Id, TenantId, ChannelUserId, Channel, Status, FailedClassificationCount, CreatedAt, LastActivityAt)
VALUES (
    @ConversationId,
    @TenantId,
    '+5491199999999',
    'whatsapp',
    0, -- Active
    0,
    DATEADD(hour, -1, SYSDATETIMEOFFSET()),
    DATEADD(minute, -5, SYSDATETIMEOFFSET())
);

-- Agregar mensajes de la conversacion (el usuario reporta un problema)
INSERT INTO ConversationMessages (Id, ConversationId, TenantId, Role, Content, CreatedAt)
VALUES (
    NEWID(),
    @ConversationId,
    @TenantId,
    0, -- User
    'Hola, tengo un problema con la aplicacion',
    DATEADD(minute, -50, SYSDATETIMEOFFSET())
);

INSERT INTO ConversationMessages (Id, ConversationId, TenantId, Role, Content, AgentName, CreatedAt)
VALUES (
    NEWID(),
    @ConversationId,
    @TenantId,
    1, -- Assistant
    'Hola! Entendido. Podrias describirme el problema que estas experimentando?',
    'SupportBot',
    DATEADD(minute, -48, SYSDATETIMEOFFSET())
);

INSERT INTO ConversationMessages (Id, ConversationId, TenantId, Role, Content, CreatedAt)
VALUES (
    NEWID(),
    @ConversationId,
    @TenantId,
    0, -- User
    'Cuando consulto los precios de los productos me muestra valores incorrectos, hay un error en los datos',
    DATEADD(minute, -45, SYSDATETIMEOFFSET())
);

INSERT INTO ConversationMessages (Id, ConversationId, TenantId, Role, Content, AgentName, CreatedAt)
VALUES (
    NEWID(),
    @ConversationId,
    @TenantId,
    1, -- Assistant
    'Entendido. He detectado que reportas un problema de datos incorrectos. Voy a crear un incidente para investigar y solucionar este error.',
    'SupportBot',
    DATEADD(minute, -43, SYSDATETIMEOFFSET())
);

-- Crear el incidente
DECLARE @IncidentId uniqueidentifier = NEWID();

INSERT INTO Incidents (
    Id,
    TenantId,
    ConversationId,
    Title,
    Description,
    Category,
    Severity,
    Status,
    AssignedTo,
    ResolutionNotes,
    CreatedAt,
    UpdatedAt
)
VALUES (
    @IncidentId,
    @TenantId,
    @ConversationId,
    'Error en precios de productos - datos incorrectos',
    'El usuario reporta que al consultar los precios de los productos en la aplicacion, los valores mostrados son incorrectos. Posible problema en la tabla de precios o en el calculo de descuentos.',
    1, -- Database
    1, -- Medium
    0, -- Open
    'bot',
    NULL,
    DATEADD(minute, -40, SYSDATETIMEOFFSET()),
    DATEADD(minute, -40, SYSDATETIMEOFFSET())
);

-- Agregar un log de diagnostico inicial
INSERT INTO DiagnosticLogs (Id, TenantId, IncidentId, StepName, Result, IsSuccess, CreatedAt)
VALUES (
    NEWID(),
    @TenantId,
    @IncidentId,
    'IncidentCreated',
    'Incidente creado automaticamente a partir de clasificacion de mensaje del usuario. Categoria: Database, Severidad: Medium',
    1,
    DATEADD(minute, -38, SYSDATETIMEOFFSET())
);

-- Agregar un adjunto con el stack trace/error reportado
INSERT INTO IncidentAttachments (Id, TenantId, IncidentId, Type, FileName, Content, CreatedAt)
VALUES (
    NEWID(),
    @TenantId,
    @IncidentId,
    2, -- StackTrace
    'error_report.txt',
    'Error: Los precios mostrados no coinciden con los almacenados en la base de datos. El usuario reporta que al ejecutar GET /api/products/price se obtienen valores incorrectos.',
    DATEADD(minute, -35, SYSDATETIMEOFFSET())
);

-- =====================================================
-- VERIFICACION
-- =====================================================

PRINT '========================================';
PRINT 'Base de datos Hive creada exitosamente!';
PRINT '========================================';
PRINT '';
PRINT 'Tenant: DeportesPrueba';
PRINT 'Usuario: deportes';
PRINT 'Password: 123456';
PRINT 'WhatsApp: +5491150000000';
PRINT 'API Key: ' + @TenantApiKey;
PRINT '';
PRINT 'Incidente de prueba creado:';
PRINT '  - Titulo: Error en precios de productos';
PRINT '  - Estado: Abierto (esperando resolucion del bot)';
PRINT '';
PRINT 'Para probar:';
PRINT '1. Iniciar HiveOps.Api';
PRINT '2. Enviar mensaje al webhook o usar Postman';
PRINT '3. El bot deberia analizar el incidente y proponer una solucion';
PRINT '========================================';
GO
