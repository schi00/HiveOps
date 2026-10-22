-- V1.5: LlmPendingTasks — durable pool of AI work interrupted by total LLM
-- outage (or any failed attempt). The retry worker replays tasks with
-- exponential backoff; dedupe keeps at most one Pending task per work item.

BEGIN;

CREATE TABLE IF NOT EXISTS "LlmPendingTasks" (
    "Id"                uuid         NOT NULL DEFAULT gen_random_uuid(),
    "TenantId"          uuid         NOT NULL,
    "TaskType"          varchar(40)  NOT NULL,
    "PayloadJson"       text         NOT NULL DEFAULT '{}',
    "DedupeKey"         varchar(300) NOT NULL,
    "Status"            varchar(20)  NOT NULL DEFAULT 'Pending',
    "Attempts"          integer      NOT NULL DEFAULT 0,
    "MaxAttempts"       integer      NOT NULL DEFAULT 12,
    "NextAttemptAtUtc"  timestamptz  NOT NULL DEFAULT now(),
    "LastError"         varchar(2000) NULL,
    "CreatedAtUtc"      timestamptz  NOT NULL DEFAULT now(),
    "UpdatedAtUtc"      timestamptz  NOT NULL DEFAULT now(),
    "CompletedAtUtc"    timestamptz  NULL,
    CONSTRAINT "PK_LlmPendingTasks" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_LPT_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id")
);

-- One live (Pending) task per work item per tenant.
CREATE UNIQUE INDEX IF NOT EXISTS "UX_LPT_Tenant_Dedupe_Pending"
    ON "LlmPendingTasks"("TenantId", "DedupeKey")
    WHERE "Status" = 'Pending';

CREATE INDEX IF NOT EXISTS "IX_LPT_Due"
    ON "LlmPendingTasks"("Status", "NextAttemptAtUtc");

CREATE INDEX IF NOT EXISTS "IX_LPT_Tenant_Status"
    ON "LlmPendingTasks"("TenantId", "Status");

COMMIT;
