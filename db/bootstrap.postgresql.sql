-- =====================================================================
-- HiveOps Support Bot - PostgreSQL bootstrap (consolidated)
-- Covers: create_hive_support_bot.sql + additive patches + V1.0 ApiDrift
--         + V1.1 Effectiveness/Embeddings + V1.2 AI Support Engineer
-- Target: PostgreSQL 14+  |  psql bootstrap.postgresql.sql
-- =====================================================================
-- Idempotent: every DDL is guarded with IF [NOT] EXISTS / ADD COLUMN IF
-- NOT EXISTS / DROP POLICY IF EXISTS so re-runs are safe.

BEGIN;

-- =====================================================================
-- BASE TABLES
-- =====================================================================

CREATE TABLE IF NOT EXISTS "Tenants" (
    "Id"              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "Name"            varchar(200) NOT NULL,
    "Industry"        varchar(100) NULL,
    "WhatsAppNumber"  varchar(30)  NULL,
    "ApiKey"          varchar(128) NOT NULL,
    "IsActive"        boolean      NOT NULL DEFAULT TRUE,
    "ConfigJson"      text         NULL,
    "EncryptedConnectionString"    text NULL,
    "Plan"                         integer NOT NULL DEFAULT 0,
    "MaxMonthlyIncidents"          integer NULL,
    "MaxUsers"                     integer NULL,
    "HasRollbackCapability"        boolean NOT NULL DEFAULT TRUE,
    "StripeCustomerId"             varchar(100) NULL,
    "StripeSubscriptionId"         varchar(100) NULL,
    "StripePriceId"                varchar(100) NULL,
    "StripeSubscriptionItemId"     varchar(100) NULL,
    "SubscriptionStatus"           varchar(50)  NULL,
    "SubscriptionCurrentPeriodEnd" timestamptz  NULL,
    "CreatedAt"       timestamptz  NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_Tenants_ApiKey" ON "Tenants"("ApiKey");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Tenants_WhatsAppNumber"
    ON "Tenants"("WhatsAppNumber") WHERE "WhatsAppNumber" IS NOT NULL;

CREATE TABLE IF NOT EXISTS "AppUsers" (
    "Id"                          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "Username"                    varchar(100) NOT NULL,
    "Email"                       varchar(200) NOT NULL,
    "PasswordHash"                text         NOT NULL,
    "Role"                        varchar(20)  NOT NULL,
    "TenantId"                    uuid         NULL,
    "IsActive"                    boolean      NOT NULL DEFAULT TRUE,
    "PasswordResetTokenHash"      varchar(128) NULL,
    "PasswordResetTokenExpiresAt" timestamptz  NULL,
    "CreatedAt"                   timestamptz  NOT NULL DEFAULT now(),
    "UpdatedAt"                   timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT "FK_AppUsers_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id") ON DELETE SET NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_AppUsers_Username" ON "AppUsers"("Username");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_AppUsers_Email"    ON "AppUsers"("Email");
CREATE INDEX        IF NOT EXISTS "IX_AppUsers_Role_IsActive" ON "AppUsers"("Role", "IsActive");

CREATE TABLE IF NOT EXISTS "Conversations" (
    "Id"                        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"                  uuid         NOT NULL,
    "ChannelUserId"             varchar(100) NOT NULL,
    "Channel"                   varchar(50)  NOT NULL,
    "Status"                    integer      NOT NULL DEFAULT 0, -- 0=Active, 1=Closed, 2=Escalated
    "FailedClassificationCount" integer      NOT NULL DEFAULT 0,
    "CreatedAt"                 timestamptz  NOT NULL DEFAULT now(),
    "LastActivityAt"            timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT "FK_Conversations_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id")
);

CREATE INDEX IF NOT EXISTS "IX_Conversations_TenantId_ChannelUserId"
    ON "Conversations"("TenantId", "ChannelUserId");

-- =====================================================================
-- INCIDENTS
-- =====================================================================

CREATE TABLE IF NOT EXISTS "Incidents" (
    "Id"              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"        uuid NOT NULL,
    "ConversationId"  uuid NULL,
    "Title"           varchar(500) NOT NULL,
    "Description"     text         NOT NULL,
    "Category"        integer      NOT NULL DEFAULT 3, -- 0=Code, 1=Database, 2=Infrastructure, 3=Other
    "Severity"        integer      NOT NULL DEFAULT 0, -- 0=Low, 1=Medium, 2=High, 3=Critical
    "Status"          integer      NOT NULL DEFAULT 0, -- 0=Open, 1=InProgress, 2=PendingInfo, 3=Resolved, 4=Closed
    "AssignedTo"      varchar(100) NOT NULL DEFAULT 'bot',
    "ResolutionNotes" text         NULL,
    "GitBranch"       varchar(200) NULL,
    "GitCommitHash"   varchar(100) NULL,
    "DeployStatus"    varchar(50)  NULL,
    "CreatedAt"       timestamptz  NOT NULL DEFAULT now(),
    "UpdatedAt"       timestamptz  NOT NULL DEFAULT now(),
    "ResolvedAt"      timestamptz  NULL,
    -- Support incident fields (AddSupportIncidentTables / entity model)
    "UserEmail"               varchar(200) NULL,
    "RequiresHumanApproval"   boolean      NOT NULL DEFAULT FALSE,
    "BackupLocation"          varchar(500) NULL,
    -- V1.1: effectiveness KPIs + dedupe
    "TriageConfidence"             double precision NULL,
    "Fingerprint"                  varchar(64)      NULL,
    "OccurrenceCount"              integer          NOT NULL DEFAULT 1,
    "ExtractedEntitiesJson"        text             NULL,
    "FinalUserResponse"            text             NULL,
    "TechnicalDiagnosticNote"      text             NULL,
    "ResolutionMode"               integer          NOT NULL DEFAULT 0,
    "EligibleForEffectivenessKpi"  boolean          NOT NULL DEFAULT FALSE,
    -- Channel origin (bidirectional ticketing)
    "ExternalTicketId"        varchar(100) NULL,
    "SourceChannel"           varchar(50)  NULL,
    -- V1.2: AI Support Engineer
    "RootCauseJson"          text         NULL,
    "VerificationQuery"      text         NULL,
    "VerificationPassed"     boolean      NULL,
    "PullRequestUrl"         varchar(500) NULL,
    "ArenaValidationStatus"  varchar(50)  NULL,
    CONSTRAINT "FK_Incidents_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id"),
    CONSTRAINT "FK_Incidents_Conversations" FOREIGN KEY ("ConversationId")
        REFERENCES "Conversations"("Id")
);

CREATE INDEX IF NOT EXISTS "IX_Incidents_TenantId"    ON "Incidents"("TenantId");
CREATE INDEX IF NOT EXISTS "IX_Incidents_Status"      ON "Incidents"("Status");
CREATE INDEX IF NOT EXISTS "IX_Incidents_CreatedAt"   ON "Incidents"("CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_Incidents_ExternalTicketId"
    ON "Incidents"("TenantId", "ExternalTicketId");
CREATE INDEX IF NOT EXISTS "IX_Incidents_Tenant_Fingerprint_Open"
    ON "Incidents"("TenantId", "Fingerprint") INCLUDE ("Status", "CreatedAt");

CREATE TABLE IF NOT EXISTS "ConversationMessages" (
    "Id"                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "ConversationId"    uuid NOT NULL,
    "IncidentId"        uuid NULL, -- optional link to the incident spawned by the conversation
    "TenantId"          uuid NOT NULL,
    "Role"              integer      NOT NULL, -- 0=User, 1=Assistant, 2=System
    "Content"           text         NOT NULL,
    "AgentName"         varchar(100) NULL,
    "ExternalMessageId" varchar(255) NULL,
    "MediaId"           varchar(200) NULL,
    "MediaMimeType"     varchar(100) NULL,
    "CreatedAt"         timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT "FK_ConversationMessages_Conversations" FOREIGN KEY ("ConversationId")
        REFERENCES "Conversations"("Id"),
    CONSTRAINT "FK_ConversationMessages_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id"),
    CONSTRAINT "FK_ConversationMessages_Incidents" FOREIGN KEY ("IncidentId")
        REFERENCES "Incidents"("Id")
);

CREATE INDEX IF NOT EXISTS "IX_ConversationMessages_ConversationId_CreatedAt"
    ON "ConversationMessages"("ConversationId", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_ConversationMessages_TenantId_ExternalMessageId"
    ON "ConversationMessages"("TenantId", "ExternalMessageId");

CREATE TABLE IF NOT EXISTS "BusinessConfigs" (
    "Id"                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"              uuid NOT NULL,
    "OpeningHours"          text         NULL,
    "Branches"              text         NULL,
    "ShippingMethods"       text         NULL,
    "ReturnPolicy"          text         NULL,
    "WelcomeMessage"        varchar(1000) NULL,
    "FallbackMessage"       varchar(1000) NULL,
    "MaxRetryBeforeHandoff" integer       NOT NULL DEFAULT 2,
    "UpdatedAt"             timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT "FK_BusinessConfigs_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id")
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_BusinessConfigs_TenantId" ON "BusinessConfigs"("TenantId");

CREATE TABLE IF NOT EXISTS "IncidentAttachments" (
    "Id"         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"   uuid NOT NULL,
    "IncidentId" uuid NOT NULL,
    "Type"       integer      NOT NULL DEFAULT 5, -- 0=Log, 1=Screenshot, 2=StackTrace, 3=SqlScript, 4=Diff, 5=Other
    "FileName"   varchar(500) NOT NULL,
    "Content"    text         NOT NULL,
    "CreatedAt"  timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT "FK_IncidentAttachments_Incidents" FOREIGN KEY ("IncidentId")
        REFERENCES "Incidents"("Id"),
    CONSTRAINT "FK_IncidentAttachments_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id")
);

CREATE INDEX IF NOT EXISTS "IX_IncidentAttachments_IncidentId"
    ON "IncidentAttachments"("IncidentId");

CREATE TABLE IF NOT EXISTS "DiagnosticLogs" (
    "Id"         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"   uuid NOT NULL,
    "IncidentId" uuid NOT NULL,
    "StepName"   varchar(200) NOT NULL,
    "Result"     text         NOT NULL,
    "IsSuccess"  boolean      NOT NULL DEFAULT TRUE,
    "CreatedAt"  timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT "FK_DiagnosticLogs_Incidents" FOREIGN KEY ("IncidentId")
        REFERENCES "Incidents"("Id"),
    CONSTRAINT "FK_DiagnosticLogs_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id")
);

CREATE INDEX IF NOT EXISTS "IX_DiagnosticLogs_IncidentId" ON "DiagnosticLogs"("IncidentId");
CREATE INDEX IF NOT EXISTS "IX_DiagnosticLogs_CreatedAt"  ON "DiagnosticLogs"("CreatedAt");

CREATE TABLE IF NOT EXISTS "KbArticles" (
    "Id"               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"         uuid NOT NULL,
    "SourceIncidentId" uuid NULL,
    "Title"            varchar(500) NOT NULL,
    "Content"          text         NOT NULL,
    "Category"         varchar(100) NOT NULL DEFAULT 'Other',
    "Tags"             varchar(500) NOT NULL DEFAULT '',
    "ResolutionSteps"  text         NOT NULL DEFAULT '',
    "IsPublished"      boolean      NOT NULL DEFAULT FALSE,
    "CreatedAt"        timestamptz  NOT NULL DEFAULT now(),
    "UpdatedAt"        timestamptz  NOT NULL DEFAULT now(),
    "EmbeddingVector"  bytea        NULL,
    CONSTRAINT "FK_KbArticles_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id")
);

CREATE INDEX IF NOT EXISTS "IX_KbArticles_TenantId"    ON "KbArticles"("TenantId");
CREATE INDEX IF NOT EXISTS "IX_KbArticles_Category"    ON "KbArticles"("Category");
CREATE INDEX IF NOT EXISTS "IX_KbArticles_IsPublished" ON "KbArticles"("IsPublished");

-- =====================================================================
-- V1.0: API DRIFT DETECTION
-- =====================================================================

CREATE TABLE IF NOT EXISTS "ApiDependencies" (
    "Id"                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"            uuid          NOT NULL,
    "ProviderName"        integer       NOT NULL DEFAULT 3, -- ApiProvider enum (int)
    "EndpointUrl"         varchar(2048) NOT NULL,
    "HttpMethod"          varchar(10)   NULL DEFAULT 'GET',
    "ExpectedSchemaHash"  varchar(64)   NOT NULL,
    "LastResponseSchema"  text          NULL,
    "LastChecked"         timestamptz   NOT NULL DEFAULT now(),
    "Status"              integer       NOT NULL DEFAULT 0, -- ApiDependencyStatus enum (int)
    "CreatedAt"           timestamptz   NOT NULL DEFAULT now(),
    "UpdatedAt"           timestamptz   NULL,
    CONSTRAINT "FK_ApiDependencies_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id") ON DELETE CASCADE
);

CREATE INDEX        IF NOT EXISTS "IX_ApiDependencies_TenantId"
    ON "ApiDependencies"("TenantId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ApiDependencies_Tenant_Endpoint"
    ON "ApiDependencies"("TenantId", "EndpointUrl");
CREATE INDEX        IF NOT EXISTS "IX_ApiDependencies_Status"
    ON "ApiDependencies"("Status") WHERE "Status" = 2; -- DriftDetected

COMMENT ON TABLE  "ApiDependencies"             IS 'Stores API dependencies for drift detection. Tracks schema hashes and alerts on contract changes.';
COMMENT ON COLUMN "ApiDependencies"."TenantId" IS 'Tenant identifier for multi-tenancy isolation';

-- =====================================================================
-- V1.2: AI SUPPORT ENGINEER FOUNDATION
-- =====================================================================

CREATE TABLE IF NOT EXISTS "IncidentRemediationSnapshots" (
    "Id"                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"              uuid NOT NULL,
    "IncidentId"            uuid NOT NULL,
    "TargetTable"           varchar(256) NOT NULL,
    "PrimaryKeyColumnsJson" text         NOT NULL DEFAULT '[]',
    "BeforeRowsJson"        text         NOT NULL DEFAULT '[]',
    "AfterRowsJson"         text         NOT NULL DEFAULT '[]',
    "InverseSqlScript"      text         NOT NULL DEFAULT '',
    "ForwardSqlScript"      text         NOT NULL DEFAULT '',
    "AffectedRowCount"      integer      NOT NULL DEFAULT 0,
    "IsReverted"            boolean      NOT NULL DEFAULT FALSE,
    "CreatedAt"             timestamptz  NOT NULL DEFAULT now(),
    "RevertedAt"            timestamptz  NULL,
    "RevertedBy"            varchar(200) NULL,
    CONSTRAINT "FK_IRS_Incidents" FOREIGN KEY ("IncidentId")
        REFERENCES "Incidents"("Id")
);

CREATE INDEX IF NOT EXISTS "IX_IRS_Incident"
    ON "IncidentRemediationSnapshots"("TenantId", "IncidentId");

CREATE TABLE IF NOT EXISTS "TenantPlaybooks" (
    "Id"                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"               uuid         NOT NULL,
    "Code"                   varchar(100) NOT NULL,
    "Name"                   varchar(200) NOT NULL,
    "TriggerSymptoms"        text         NOT NULL DEFAULT '',
    "ActionType"             varchar(50)  NOT NULL DEFAULT 'SqlTemplate',
    "ActionPayloadTemplate"  text         NOT NULL,
    "RequiredParametersJson" text         NOT NULL DEFAULT '[]',
    "VerificationSqlQuery"   text         NULL,
    "MaxAffectedRows"        integer      NOT NULL DEFAULT 5,
    "RequiresApproval"       boolean      NOT NULL DEFAULT TRUE,
    "IsActive"               boolean      NOT NULL DEFAULT TRUE,
    "CreatedAt"              timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT "FK_TP_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id")
);

CREATE UNIQUE INDEX IF NOT EXISTS "UX_TP_Tenant_Code"
    ON "TenantPlaybooks"("TenantId", "Code");

CREATE TABLE IF NOT EXISTS "IncidentAuditEntries" (
    "Id"             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"       uuid         NOT NULL,
    "IncidentId"     uuid         NOT NULL,
    "SequenceNumber" integer      NOT NULL,
    "EventType"      varchar(50)  NOT NULL,
    "Actor"          varchar(200) NOT NULL DEFAULT 'HiveOps.AutoEngine',
    "PayloadJson"    text         NOT NULL DEFAULT '{}',
    "PreviousHash"   varchar(128) NOT NULL,
    "EntryHash"      varchar(128) NOT NULL,
    "CreatedAt"      timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT "FK_IAE_Incidents" FOREIGN KEY ("IncidentId")
        REFERENCES "Incidents"("Id")
);

CREATE INDEX IF NOT EXISTS "IX_IAE_Incident"
    ON "IncidentAuditEntries"("TenantId", "IncidentId", "SequenceNumber");

-- =====================================================================
-- ROW-LEVEL SECURITY (tenant isolation safety net)
-- =====================================================================
-- The app sets the session GUC  app.tenant_id  on every pooled connection
-- (see TenantSessionContextInterceptor: set_config('app.tenant_id', ...)).
-- Policies below deny all rows when the GUC is unset (secure default).
--
-- IMPORTANT: PostgreSQL table owners and superusers BYPASS RLS. To make
-- these policies effective for the application connection, connect with a
-- dedicated non-owner role and FORCE ROW LEVEL SECURITY:
--   CREATE ROLE hiveops_app LOGIN PASSWORD '...';
--   GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO hiveops_app;
--   ALTER TABLE <t> FORCE ROW LEVEL SECURITY;  (per tenant table)
-- Until then the policies act as documented enforcement for non-owner roles.

DO $$
DECLARE
    t text;
    tenant_tables text[] := ARRAY[
        'Conversations', 'ConversationMessages', 'BusinessConfigs',
        'Incidents', 'IncidentAttachments', 'DiagnosticLogs',
        'KbArticles', 'ApiDependencies',
        'IncidentRemediationSnapshots', 'TenantPlaybooks', 'IncidentAuditEntries'
    ];
BEGIN
    FOREACH t IN ARRAY tenant_tables LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON %I', t);
        EXECUTE format(
            'CREATE POLICY tenant_isolation ON %I
             USING ("TenantId" = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid)',
            t);
    END LOOP;
END $$;

COMMIT;

-- =====================================================================
-- SEED DATA (idempotent via stable GUIDs)
-- =====================================================================

-- Tenant de prueba: DeportesPrueba
INSERT INTO "Tenants" ("Id", "Name", "Industry", "WhatsAppNumber", "ApiKey", "IsActive", "ConfigJson", "CreatedAt")
VALUES (
    '11111111-1111-1111-1111-111111111111',
    'DeportesPrueba',
    'deportes',
    '+5491150000000',
    'deportes-api-key-12345678',
    TRUE,
    '{"WelcomeMessage": "Hola! Soy el asistente de soporte de DeportesPrueba. Como puedo ayudarte?", "FallbackMessage": "Disculpa, no entendi tu mensaje. Puedes reformularlo o escribir hablar con persona para hablar con un agente.", "MaxRetryBeforeHandoff": 2, "AllowedIntents": ["Greeting", "HumanHandoff", "IncidentReport", "IncidentQuery", "IncidentApprove", "IncidentReject", "DeployRequest", "Unknown"]}',
    now()
)
ON CONFLICT ("Id") DO NOTHING;

-- Usuario de prueba: deportes / 123456 (PBKDF2, 100000 iteraciones, SHA256)
INSERT INTO "AppUsers" ("Id", "Username", "Email", "PasswordHash", "Role", "TenantId", "IsActive", "CreatedAt", "UpdatedAt")
VALUES (
    'aaaaaaaa-0000-0000-0000-000000000001',
    'deportes',
    'deportes@deportesprueba.local',
    'v1.100000.Qv0McvSDZ3OhGqAAT8RvMQ==.xrC/WQLxtC0y5pjBM0c4SZ2S5BXCX0nXQJJmNt9MFGk=',
    'Tenant',
    '11111111-1111-1111-1111-111111111111',
    TRUE,
    now(),
    now()
)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "BusinessConfigs" ("Id", "TenantId", "OpeningHours", "Branches", "WelcomeMessage", "FallbackMessage", "MaxRetryBeforeHandoff", "UpdatedAt")
VALUES (
    'bbbbbbbb-0000-0000-0000-000000000001',
    '11111111-1111-1111-1111-111111111111',
    '{"monday":"9:00-18:00","tuesday":"9:00-18:00","wednesday":"9:00-18:00","thursday":"9:00-18:00","friday":"9:00-17:00"}',
    '[{"name":"Casa Central","address":"Av. Principal 123","phone":"+5491112345678"}]',
    'Hola! Soy el asistente de soporte de DeportesPrueba. Como puedo ayudarte hoy?',
    'Disculpa, no entendi tu mensaje. Escribe "hablar con persona" para contactar un agente.',
    2,
    now()
)
ON CONFLICT ("Id") DO NOTHING;

-- Escenario de prueba: conversación + incidente abierto
INSERT INTO "Conversations" ("Id", "TenantId", "ChannelUserId", "Channel", "Status", "FailedClassificationCount", "CreatedAt", "LastActivityAt")
VALUES (
    'cccccccc-0000-0000-0000-000000000001',
    '11111111-1111-1111-1111-111111111111',
    '+5491199999999',
    'whatsapp',
    0,
    0,
    now() - interval '1 hour',
    now() - interval '5 minutes'
)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "ConversationMessages" ("Id", "ConversationId", "TenantId", "Role", "Content", "CreatedAt") VALUES
('dddddddd-0000-0000-0000-000000000001', 'cccccccc-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111', 0, 'Hola, tengo un problema con la aplicacion', now() - interval '50 minutes'),
('dddddddd-0000-0000-0000-000000000002', 'cccccccc-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111', 1, 'Hola! Entendido. Podrias describirme el problema que estas experimentando?', now() - interval '48 minutes'),
('dddddddd-0000-0000-0000-000000000003', 'cccccccc-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111', 0, 'Cuando consulto los precios de los productos me muestra valores incorrectos, hay un error en los datos', now() - interval '45 minutes')
ON CONFLICT ("Id") DO NOTHING;

UPDATE "ConversationMessages"
SET "AgentName" = 'SupportBot'
WHERE "Id" = 'dddddddd-0000-0000-0000-000000000002' AND "AgentName" IS NULL;

INSERT INTO "Incidents" ("Id", "TenantId", "ConversationId", "Title", "Description", "Category", "Severity", "Status", "AssignedTo", "CreatedAt", "UpdatedAt")
VALUES (
    'eeeeeeee-0000-0000-0000-000000000001',
    '11111111-1111-1111-1111-111111111111',
    'cccccccc-0000-0000-0000-000000000001',
    'Error en precios de productos - datos incorrectos',
    'El usuario reporta que al consultar los precios de los productos en la aplicacion, los valores mostrados son incorrectos. Posible problema en la tabla de precios o en el calculo de descuentos.',
    1, -- Database
    1, -- Medium
    0, -- Open
    'bot',
    now() - interval '40 minutes',
    now() - interval '40 minutes'
)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "DiagnosticLogs" ("Id", "TenantId", "IncidentId", "StepName", "Result", "IsSuccess", "CreatedAt")
VALUES (
    'ffffffff-0000-0000-0000-000000000001',
    '11111111-1111-1111-1111-111111111111',
    'eeeeeeee-0000-0000-0000-000000000001',
    'IncidentCreated',
    'Incidente creado automaticamente a partir de clasificacion de mensaje del usuario. Categoria: Database, Severidad: Medium',
    TRUE,
    now() - interval '38 minutes'
)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "IncidentAttachments" ("Id", "TenantId", "IncidentId", "Type", "FileName", "Content", "CreatedAt")
VALUES (
    '99999999-0000-0000-0000-000000000001',
    '11111111-1111-1111-1111-111111111111',
    'eeeeeeee-0000-0000-0000-000000000001',
    2, -- StackTrace
    'error_report.txt',
    'Error: Los precios mostrados no coinciden con los almacenados en la base de datos. El usuario reporta que al ejecutar GET /api/products/price se obtienen valores incorrectos.',
    now() - interval '35 minutes'
)
ON CONFLICT ("Id") DO NOTHING;

DO $$
BEGIN
    RAISE NOTICE '========================================';
    RAISE NOTICE 'HiveOps PostgreSQL bootstrap complete';
    RAISE NOTICE 'Tenant: DeportesPrueba | User: deportes / 123456';
    RAISE NOTICE 'API Key: deportes-api-key-12345678';
    RAISE NOTICE '========================================';
END $$;
