SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

-- V1.2: AI Support Engineer foundation.
--   * IncidentRemediationSnapshots: row-level before/after JSON + inverse SQL
--   * TenantPlaybooks: deterministic pre-approved runbooks per tenant
--   * IncidentAuditEntries: SHA-256 hash-chained evidence ledger
--   * Incidents: root-cause bundle, verification query/result, PR + arena status
BEGIN TRY
    BEGIN TRAN;

    -- ── New columns on Incidents ──────────────────────────────────────────
    IF COL_LENGTH(N'dbo.Incidents', N'RootCauseJson') IS NULL
        ALTER TABLE dbo.Incidents ADD RootCauseJson NVARCHAR(MAX) NULL;

    IF COL_LENGTH(N'dbo.Incidents', N'VerificationQuery') IS NULL
        ALTER TABLE dbo.Incidents ADD VerificationQuery NVARCHAR(MAX) NULL;

    IF COL_LENGTH(N'dbo.Incidents', N'VerificationPassed') IS NULL
        ALTER TABLE dbo.Incidents ADD VerificationPassed BIT NULL;

    IF COL_LENGTH(N'dbo.Incidents', N'PullRequestUrl') IS NULL
        ALTER TABLE dbo.Incidents ADD PullRequestUrl NVARCHAR(500) NULL;

    IF COL_LENGTH(N'dbo.Incidents', N'ArenaValidationStatus') IS NULL
        ALTER TABLE dbo.Incidents ADD ArenaValidationStatus NVARCHAR(50) NULL;

    -- ── IncidentRemediationSnapshots ──────────────────────────────────────
    IF OBJECT_ID(N'dbo.IncidentRemediationSnapshots', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.IncidentRemediationSnapshots
        (
            Id                   UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_IncidentRemediationSnapshots PRIMARY KEY,
            TenantId             UNIQUEIDENTIFIER NOT NULL,
            IncidentId           UNIQUEIDENTIFIER NOT NULL,
            TargetTable          NVARCHAR(256)    NOT NULL,
            PrimaryKeyColumnsJson NVARCHAR(MAX)   NOT NULL CONSTRAINT DF_IRS_PkCols DEFAULT N'[]',
            BeforeRowsJson       NVARCHAR(MAX)    NOT NULL CONSTRAINT DF_IRS_Before DEFAULT N'[]',
            AfterRowsJson        NVARCHAR(MAX)    NOT NULL CONSTRAINT DF_IRS_After  DEFAULT N'[]',
            InverseSqlScript     NVARCHAR(MAX)    NOT NULL CONSTRAINT DF_IRS_Inverse DEFAULT N'',
            ForwardSqlScript     NVARCHAR(MAX)    NOT NULL CONSTRAINT DF_IRS_Forward DEFAULT N'',
            AffectedRowCount     INT              NOT NULL CONSTRAINT DF_IRS_Rows DEFAULT 0,
            IsReverted           BIT              NOT NULL CONSTRAINT DF_IRS_Reverted DEFAULT 0,
            CreatedAt            DATETIMEOFFSET   NOT NULL CONSTRAINT DF_IRS_Created DEFAULT SYSDATETIMEOFFSET(),
            RevertedAt           DATETIMEOFFSET   NULL,
            RevertedBy           NVARCHAR(200)    NULL,
            CONSTRAINT FK_IRS_Incidents FOREIGN KEY (IncidentId)
                REFERENCES dbo.Incidents(Id)
        );

        CREATE INDEX IX_IRS_Incident ON dbo.IncidentRemediationSnapshots(TenantId, IncidentId);
    END;

    -- ── TenantPlaybooks ───────────────────────────────────────────────────
    IF OBJECT_ID(N'dbo.TenantPlaybooks', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.TenantPlaybooks
        (
            Id                     UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_TenantPlaybooks PRIMARY KEY,
            TenantId               UNIQUEIDENTIFIER NOT NULL,
            Code                   NVARCHAR(100)    NOT NULL,
            Name                   NVARCHAR(200)    NOT NULL,
            TriggerSymptoms        NVARCHAR(MAX)    NOT NULL CONSTRAINT DF_TP_Symptoms DEFAULT N'',
            ActionType             NVARCHAR(50)     NOT NULL CONSTRAINT DF_TP_ActionType DEFAULT N'SqlTemplate',
            ActionPayloadTemplate  NVARCHAR(MAX)    NOT NULL,
            RequiredParametersJson NVARCHAR(MAX)    NOT NULL CONSTRAINT DF_TP_Params DEFAULT N'[]',
            VerificationSqlQuery   NVARCHAR(MAX)    NULL,
            MaxAffectedRows        INT              NOT NULL CONSTRAINT DF_TP_MaxRows DEFAULT 5,
            RequiresApproval       BIT              NOT NULL CONSTRAINT DF_TP_RequiresApproval DEFAULT 1,
            IsActive               BIT              NOT NULL CONSTRAINT DF_TP_Active DEFAULT 1,
            CreatedAt              DATETIMEOFFSET   NOT NULL CONSTRAINT DF_TP_Created DEFAULT SYSDATETIMEOFFSET(),
            CONSTRAINT FK_TP_Tenants FOREIGN KEY (TenantId)
                REFERENCES dbo.Tenants(Id)
        );

        CREATE UNIQUE INDEX UX_TP_Tenant_Code ON dbo.TenantPlaybooks(TenantId, Code);
    END;

    -- ── IncidentAuditEntries ──────────────────────────────────────────────
    IF OBJECT_ID(N'dbo.IncidentAuditEntries', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.IncidentAuditEntries
        (
            Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_IncidentAuditEntries PRIMARY KEY,
            TenantId       UNIQUEIDENTIFIER NOT NULL,
            IncidentId     UNIQUEIDENTIFIER NOT NULL,
            SequenceNumber INT              NOT NULL,
            EventType      NVARCHAR(50)     NOT NULL,
            Actor          NVARCHAR(200)    NOT NULL CONSTRAINT DF_IAE_Actor DEFAULT N'HiveOps.AutoEngine',
            PayloadJson    NVARCHAR(MAX)    NOT NULL CONSTRAINT DF_IAE_Payload DEFAULT N'{}',
            PreviousHash   NVARCHAR(128)    NOT NULL,
            EntryHash      NVARCHAR(128)    NOT NULL,
            CreatedAt      DATETIMEOFFSET   NOT NULL CONSTRAINT DF_IAE_Created DEFAULT SYSDATETIMEOFFSET(),
            CONSTRAINT FK_IAE_Incidents FOREIGN KEY (IncidentId)
                REFERENCES dbo.Incidents(Id)
        );

        CREATE INDEX IX_IAE_Incident ON dbo.IncidentAuditEntries(TenantId, IncidentId, SequenceNumber);
    END;

    COMMIT TRAN;
    PRINT 'AI Support Engineer foundation schema applied successfully.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    THROW;
END CATCH;
