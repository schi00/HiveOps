SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

-- Additive patch: inbound media metadata (WhatsApp/ticketing attachments) on messages.
BEGIN TRY
    BEGIN TRAN;

    IF COL_LENGTH(N'dbo.ConversationMessages', N'MediaId') IS NULL
    BEGIN
        ALTER TABLE dbo.ConversationMessages
            ADD MediaId NVARCHAR(200) NULL;
    END;

    IF COL_LENGTH(N'dbo.ConversationMessages', N'MediaMimeType') IS NULL
    BEGIN
        ALTER TABLE dbo.ConversationMessages
            ADD MediaMimeType NVARCHAR(100) NULL;
    END;

    COMMIT TRAN;
    PRINT 'ConversationMessage media fields added successfully.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    THROW;
END CATCH;
