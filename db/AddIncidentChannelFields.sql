SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

-- Additive patch: origin-channel fields for bidirectional ticketing support.
BEGIN TRY
    BEGIN TRAN;

    IF COL_LENGTH(N'dbo.Incidents', N'ExternalTicketId') IS NULL
    BEGIN
        ALTER TABLE dbo.Incidents
            ADD ExternalTicketId NVARCHAR(100) NULL;
    END;

    IF COL_LENGTH(N'dbo.Incidents', N'SourceChannel') IS NULL
    BEGIN
        ALTER TABLE dbo.Incidents
            ADD SourceChannel NVARCHAR(50) NULL;
    END;

    -- Lookups by external ticket id are scoped per tenant (inbound ticket webhooks).
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Incidents_ExternalTicketId' AND object_id = OBJECT_ID(N'dbo.Incidents'))
    BEGIN
        CREATE INDEX IX_Incidents_ExternalTicketId ON dbo.Incidents(TenantId, ExternalTicketId);
    END;

    COMMIT TRAN;
    PRINT 'Incident channel fields added successfully.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    THROW;
END CATCH;
