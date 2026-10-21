-- Incident effectiveness KPI fields, dual responses, dedupe fingerprint, KB embeddings.
-- Safe to run once per environment (idempotent-style checks left to operator if needed).

IF COL_LENGTH('dbo.Incidents', 'TriageConfidence') IS NULL
    ALTER TABLE dbo.Incidents ADD TriageConfidence FLOAT NULL;

IF COL_LENGTH('dbo.Incidents', 'Fingerprint') IS NULL
    ALTER TABLE dbo.Incidents ADD Fingerprint NVARCHAR(64) NULL;

IF COL_LENGTH('dbo.Incidents', 'OccurrenceCount') IS NULL
    ALTER TABLE dbo.Incidents ADD OccurrenceCount INT NOT NULL CONSTRAINT DF_Incidents_OccurrenceCount DEFAULT (1);

IF COL_LENGTH('dbo.Incidents', 'ExtractedEntitiesJson') IS NULL
    ALTER TABLE dbo.Incidents ADD ExtractedEntitiesJson NVARCHAR(MAX) NULL;

IF COL_LENGTH('dbo.Incidents', 'FinalUserResponse') IS NULL
    ALTER TABLE dbo.Incidents ADD FinalUserResponse NVARCHAR(MAX) NULL;

IF COL_LENGTH('dbo.Incidents', 'TechnicalDiagnosticNote') IS NULL
    ALTER TABLE dbo.Incidents ADD TechnicalDiagnosticNote NVARCHAR(MAX) NULL;

IF COL_LENGTH('dbo.Incidents', 'ResolutionMode') IS NULL
    ALTER TABLE dbo.Incidents ADD ResolutionMode INT NOT NULL CONSTRAINT DF_Incidents_ResolutionMode DEFAULT (0);

IF COL_LENGTH('dbo.Incidents', 'EligibleForEffectivenessKpi') IS NULL
    ALTER TABLE dbo.Incidents ADD EligibleForEffectivenessKpi BIT NOT NULL CONSTRAINT DF_Incidents_EligibleForEffectivenessKpi DEFAULT (0);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Incidents_Tenant_Fingerprint_Open' AND object_id = OBJECT_ID('dbo.Incidents'))
    CREATE NONCLUSTERED INDEX IX_Incidents_Tenant_Fingerprint_Open
    ON dbo.Incidents (TenantId, Fingerprint)
    INCLUDE (Status, CreatedAt);

IF COL_LENGTH('dbo.KbArticles', 'EmbeddingVector') IS NULL
    ALTER TABLE dbo.KbArticles ADD EmbeddingVector VARBINARY(MAX) NULL;
