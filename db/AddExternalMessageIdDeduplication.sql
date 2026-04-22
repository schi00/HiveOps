-- Migration: Add ExternalMessageId deduplication support to ConversationMessages
-- Purpose: Enable webhook deduplication to prevent duplicate Messages from Meta retries
-- Created: 2026-04-12

IF OBJECT_ID(N'dbo.ConversationMessages', N'U') IS NOT NULL
BEGIN
    -- Step 1: Check if ExternalMessageId column already exists
    IF NOT EXISTS (
        SELECT 1 
        FROM INFORMATION_SCHEMA.COLUMNS 
        WHERE TABLE_NAME = 'ConversationMessages' 
        AND COLUMN_NAME = 'ExternalMessageId'
    )
    BEGIN
        -- Add the ExternalMessageId column
        ALTER TABLE dbo.ConversationMessages
        ADD ExternalMessageId NVARCHAR(255) NULL;
        
        PRINT 'Column ExternalMessageId added to ConversationMessages.';
    END
    ELSE
    BEGIN
        PRINT 'Column ExternalMessageId already exists.';
    END

    -- Step 2: Create index for deduplication if it doesn't exist
    IF NOT EXISTS (
        SELECT 1 
        FROM sys.indexes 
        WHERE object_id = OBJECT_ID(N'dbo.ConversationMessages')
        AND name = 'IX_ConversationMessages_TenantId_ExternalMessageId'
    )
    BEGIN
        CREATE INDEX IX_ConversationMessages_TenantId_ExternalMessageId 
        ON dbo.ConversationMessages(TenantId, ExternalMessageId);
        
        PRINT 'Index IX_ConversationMessages_TenantId_ExternalMessageId created for fast deduplication lookups.';
    END
    ELSE
    BEGIN
        PRINT 'Index IX_ConversationMessages_TenantId_ExternalMessageId already exists.';
    END
END
ELSE
BEGIN
    PRINT 'WARNING: Table dbo.ConversationMessages does not exist.';
END

-- Verification query (run after migration to verify)
-- SELECT COUNT(*) as TotalMessages, 
--        COUNT(DISTINCT ExternalMessageId) as UniqueExternalIds,
--        COUNT(CASE WHEN ExternalMessageId IS NOT NULL THEN 1 END) as MessagesWithExternalId
-- FROM dbo.ConversationMessages;
