SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRY
    BEGIN TRAN;

    -- ── Incidents ───────────────────────────────────────────────────────────
    IF OBJECT_ID(N'dbo.Incidents', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Incidents
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Incidents PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            ConversationId UNIQUEIDENTIFIER NOT NULL,
            Title NVARCHAR(500) NOT NULL,
            Description NVARCHAR(MAX) NOT NULL,
            Category NVARCHAR(50) NOT NULL CONSTRAINT DF_Incidents_Category DEFAULT (N'Other'),
            Severity NVARCHAR(50) NOT NULL CONSTRAINT DF_Incidents_Severity DEFAULT (N'Low'),
            Status NVARCHAR(50) NOT NULL CONSTRAINT DF_Incidents_Status DEFAULT (N'Open'),
            AssignedTo NVARCHAR(100) NOT NULL CONSTRAINT DF_Incidents_AssignedTo DEFAULT (N'bot'),
            ResolutionNotes NVARCHAR(MAX) NULL,
            GitBranch NVARCHAR(200) NULL,
            GitCommitHash NVARCHAR(100) NULL,
            DeployStatus NVARCHAR(100) NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Incidents_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            UpdatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_Incidents_UpdatedAt DEFAULT (SYSDATETIMEOFFSET()),
            ResolvedAt DATETIMEOFFSET(7) NULL,
            CONSTRAINT FK_Incidents_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE CASCADE,
            CONSTRAINT FK_Incidents_Conversations FOREIGN KEY (ConversationId) REFERENCES dbo.Conversations(Id) ON DELETE CASCADE
        );

        CREATE INDEX IX_Incidents_TenantId ON dbo.Incidents(TenantId);
        CREATE INDEX IX_Incidents_ConversationId ON dbo.Incidents(ConversationId);
        CREATE INDEX IX_Incidents_Status ON dbo.Incidents(Status);
        CREATE INDEX IX_Incidents_Category ON dbo.Incidents(Category);
    END;

    -- ── IncidentAttachments ───────────────────────────────────────────────
    IF OBJECT_ID(N'dbo.IncidentAttachments', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.IncidentAttachments
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_IncidentAttachments PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            IncidentId UNIQUEIDENTIFIER NOT NULL,
            Type NVARCHAR(50) NOT NULL CONSTRAINT DF_IncidentAttachments_Type DEFAULT (N'Other'),
            FileName NVARCHAR(500) NOT NULL,
            Content NVARCHAR(MAX) NOT NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_IncidentAttachments_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_IncidentAttachments_Incidents FOREIGN KEY (IncidentId) REFERENCES dbo.Incidents(Id) ON DELETE CASCADE
        );

        CREATE INDEX IX_IncidentAttachments_IncidentId ON dbo.IncidentAttachments(IncidentId);
        CREATE INDEX IX_IncidentAttachments_TenantId ON dbo.IncidentAttachments(TenantId);
    END;

    -- ── DiagnosticLogs ────────────────────────────────────────────────────
    IF OBJECT_ID(N'dbo.DiagnosticLogs', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.DiagnosticLogs
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_DiagnosticLogs PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            IncidentId UNIQUEIDENTIFIER NOT NULL,
            StepName NVARCHAR(200) NOT NULL,
            Result NVARCHAR(MAX) NOT NULL,
            IsSuccess BIT NOT NULL CONSTRAINT DF_DiagnosticLogs_IsSuccess DEFAULT (0),
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_DiagnosticLogs_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_DiagnosticLogs_Incidents FOREIGN KEY (IncidentId) REFERENCES dbo.Incidents(Id) ON DELETE CASCADE
        );

        CREATE INDEX IX_DiagnosticLogs_IncidentId ON dbo.DiagnosticLogs(IncidentId);
        CREATE INDEX IX_DiagnosticLogs_TenantId ON dbo.DiagnosticLogs(TenantId);
    END;

    -- ── KbArticles ──────────────────────────────────────────────────────────
    IF OBJECT_ID(N'dbo.KbArticles', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.KbArticles
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_KbArticles PRIMARY KEY,
            TenantId UNIQUEIDENTIFIER NOT NULL,
            Title NVARCHAR(500) NOT NULL,
            Content NVARCHAR(MAX) NOT NULL,
            Category NVARCHAR(100) NOT NULL CONSTRAINT DF_KbArticles_Category DEFAULT (N'General'),
            Tags NVARCHAR(MAX) NULL CONSTRAINT DF_KbArticles_Tags DEFAULT (N'[]'),
            IsPublished BIT NOT NULL CONSTRAINT DF_KbArticles_IsPublished DEFAULT (0),
            ResolutionSteps NVARCHAR(MAX) NULL,
            CreatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_KbArticles_CreatedAt DEFAULT (SYSDATETIMEOFFSET()),
            UpdatedAt DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_KbArticles_UpdatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT FK_KbArticles_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id) ON DELETE CASCADE
        );

        CREATE INDEX IX_KbArticles_TenantId ON dbo.KbArticles(TenantId);
        CREATE INDEX IX_KbArticles_Category ON dbo.KbArticles(Category);
        CREATE INDEX IX_KbArticles_IsPublished ON dbo.KbArticles(IsPublished);
    END;

    COMMIT TRAN;
    PRINT 'Support incident tables created successfully.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    THROW;
END CATCH;
