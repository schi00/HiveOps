-- =====================================================================
-- V1.4: TENANT IMPROVEMENT SUGGESTIONS
-- =====================================================================
--   * TenantImprovementSuggestions: self-improvement queue — every agent
--                                   failure auto-files an improvement
--                                   ticket (new playbook, KB gap, data
--                                   quality rule, planner gap). Humans
--                                   review, then convert to a playbook
--                                   or dismiss.
--
-- Target: PostgreSQL 14+  |  psql V1.4__TenantImprovementSuggestions.sql
-- Idempotent: every DDL is guarded with IF NOT EXISTS so re-runs are safe.
-- This DDL is also inlined in bootstrap.postgresql.sql (same section).

BEGIN;

CREATE TABLE IF NOT EXISTS "TenantImprovementSuggestions" (
    "Id"                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId"              uuid         NOT NULL,
    "IncidentId"            uuid         NULL, -- source incident; NULL = manually generated
    "Kind"                  text         NOT NULL DEFAULT 'NewPlaybook', -- NewPlaybook | KbGap | DataQualityRule | PlannerGap
    "Title"                 text         NOT NULL DEFAULT '',
    "Description"           text         NOT NULL DEFAULT '',
    "EvidenceJson"          text         NOT NULL DEFAULT '{}', -- { "symptoms","failedAttempts":[],"escalationNotes","relatedTerms":[] }
    "DraftPayloadJson"      text         NOT NULL DEFAULT '{}', -- proposed artifact (playbook/kb article/dq rule)
    "Status"                text         NOT NULL DEFAULT 'Suggested', -- Suggested | Dismissed | Converted
    "OccurrenceCount"       integer      NOT NULL DEFAULT 1,
    "ConvertedPlaybookId"   uuid         NULL, -- TenantPlaybooks.Id created on conversion
    "CreatedBy"             varchar(200) NULL,
    "DecidedBy"             varchar(200) NULL,
    "CreatedAtUtc"          timestamptz  NOT NULL DEFAULT now(),
    "UpdatedAtUtc"          timestamptz  NOT NULL DEFAULT now(),
    "DecidedAtUtc"          timestamptz  NULL,
    CONSTRAINT "FK_TIS_Tenants" FOREIGN KEY ("TenantId")
        REFERENCES "Tenants"("Id"),
    CONSTRAINT "FK_TIS_Incidents" FOREIGN KEY ("IncidentId")
        REFERENCES "Incidents"("Id"),
    CONSTRAINT "FK_TIS_Playbooks" FOREIGN KEY ("ConvertedPlaybookId")
        REFERENCES "TenantPlaybooks"("Id")
);

CREATE INDEX IF NOT EXISTS "IX_TIS_TenantId"
    ON "TenantImprovementSuggestions"("TenantId");
CREATE INDEX IF NOT EXISTS "IX_TIS_Incident"
    ON "TenantImprovementSuggestions"("TenantId", "IncidentId");
CREATE INDEX IF NOT EXISTS "IX_TIS_Status"
    ON "TenantImprovementSuggestions"("TenantId", "Status");

COMMIT;
