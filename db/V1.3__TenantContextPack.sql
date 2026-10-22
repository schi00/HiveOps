-- =====================================================================
-- V1.3: TENANT CONTEXT PACK
-- =====================================================================
--   * TenantContextPacks:    one-per-tenant knowledge pack (glossary,
--                            database profile, repo profiles, resolution
--                            memory) + pre-rendered prompt digest
--   * TenantDocuments:       uploaded text docs distilled into the pack
--   * TenantDocumentChunks:  ordered text chunks per document (cascade)
--
-- Target: PostgreSQL 14+  |  psql V1.3__TenantContextPack.sql
-- Idempotent: every DDL is guarded with IF NOT EXISTS so re-runs are safe.
-- This DDL is also inlined in bootstrap.postgresql.sql (same section).

BEGIN;

CREATE TABLE IF NOT EXISTS "TenantContextPacks" (
    "Id"                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"              uuid         NOT NULL,
    "Version"               integer      NOT NULL DEFAULT 1,
    "DomainGlossaryJson"    text         NOT NULL DEFAULT '{}',
    "DatabaseProfileJson"   text         NOT NULL DEFAULT '{}',
    "RepoProfilesJson"      text         NOT NULL DEFAULT '{}',
    "ResolutionMemoryJson"  text         NOT NULL DEFAULT '[]',
    "PromptDigest"          text         NOT NULL DEFAULT '',
    "CreatedAtUtc"          timestamptz  NOT NULL DEFAULT now(),
    "UpdatedAtUtc"          timestamptz  NOT NULL DEFAULT now(),
    "UpdatedBy"             varchar(200) NULL,
    CONSTRAINT "FK_TCP_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id")
);

-- One pack per tenant.
CREATE UNIQUE INDEX IF NOT EXISTS "UX_TCP_Tenant"
    ON "TenantContextPacks"("TenantId");

CREATE TABLE IF NOT EXISTS "TenantDocuments" (
    "Id"               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"         uuid         NOT NULL,
    "FileName"         text         NOT NULL,
    "ContentType"      text         NOT NULL,
    "SizeBytes"        bigint       NOT NULL DEFAULT 0,
    "Status"           text         NOT NULL DEFAULT 'Pending', -- Pending | Distilled | Failed
    "DistilledSummary" text         NULL,
    "UploadedBy"       varchar(200) NULL,
    "UploadedAtUtc"    timestamptz  NOT NULL DEFAULT now(),
    "ProcessedAtUtc"   timestamptz  NULL,
    "ChunkCount"       integer      NOT NULL DEFAULT 0,
    CONSTRAINT "FK_TD_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id")
);

CREATE INDEX IF NOT EXISTS "IX_TD_TenantId"
    ON "TenantDocuments"("TenantId");

CREATE TABLE IF NOT EXISTS "TenantDocumentChunks" (
    "Id"           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"     uuid        NOT NULL,
    "DocumentId"   uuid        NOT NULL,
    "ChunkIndex"   integer     NOT NULL,
    "SectionTitle" text        NULL,
    "Content"      text        NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "FK_TDC_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id"),
    CONSTRAINT "FK_TDC_Documents" FOREIGN KEY ("DocumentId")
        REFERENCES "TenantDocuments"("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_TDC_TenantId"
    ON "TenantDocumentChunks"("TenantId");
CREATE INDEX IF NOT EXISTS "IX_TDC_Document"
    ON "TenantDocumentChunks"("TenantId", "DocumentId", "ChunkIndex");

COMMIT;
