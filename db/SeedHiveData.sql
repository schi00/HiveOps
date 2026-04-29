-- Seed test data for Hive Database
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

-- Use existing tenant or create new ones
DECLARE @ExistingTenantId UNIQUEIDENTIFIER;
SELECT TOP 1 @ExistingTenantId = Id FROM Tenants;

-- If no tenant exists, create test tenants
IF @ExistingTenantId IS NULL
BEGIN
    DECLARE @TenantId1 UNIQUEIDENTIFIER = NEWID();
    DECLARE @TenantId2 UNIQUEIDENTIFIER = NEWID();
    DECLARE @TenantId3 UNIQUEIDENTIFIER = NEWID();

    INSERT INTO Tenants (Id, Name, Industry, ApiKey, IsActive, ConfigJson, CreatedAt)
    VALUES 
      (@TenantId1, 'Acme Corp', 'Technology', 'key_' + CONVERT(VARCHAR(50), NEWID()), 1, 
       '{"runtimeConfiguration":{"agent":{"model":"gpt-4","temperature":0.7,"maxTokens":2000},"database":{"enabled":true,"connectionString":""}}}',
       GETUTCDATE()),
      (@TenantId2, 'Global Solutions', 'Finance', 'key_' + CONVERT(VARCHAR(50), NEWID()), 1,
       '{"runtimeConfiguration":{"agent":{"model":"gpt-3.5-turbo","temperature":0.5,"maxTokens":1000},"database":{"enabled":false,"connectionString":""}}}',
       GETUTCDATE()),
      (@TenantId3, 'Contoso Retail', 'Retail', 'key_' + CONVERT(VARCHAR(50), NEWID()), 1,
       '{"runtimeConfiguration":{"agent":{"model":"gpt-4","temperature":0.6,"maxTokens":1500},"database":{"enabled":true,"connectionString":""}}}',
       GETUTCDATE());

    SET @ExistingTenantId = @TenantId1;
END

-- Ensure a tenant user exists per tenant with password to be set by app bootstrap
INSERT INTO AppUsers (Id, Username, Email, Role, TenantId, PasswordHash, IsActive, CreatedAt, UpdatedAt)
SELECT NEWID(), CONCAT('tenant_', LOWER(REPLACE(Name,' ', '_'))), CONCAT(CONCAT('tenant_', LOWER(REPLACE(Name,' ', '_'))),'@HiveOps.local'), 'Tenant', Id,
       'v1.100000.YWJjZGVmZ2hpamtsbW5vcA==.YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo=', 1, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
FROM Tenants t
WHERE NOT EXISTS (SELECT 1 FROM AppUsers u WHERE u.TenantId = t.Id AND u.Role = 'Tenant');

-- Insert test conversations
DECLARE @ConvId1 UNIQUEIDENTIFIER = NEWID();
DECLARE @ConvId2 UNIQUEIDENTIFIER = NEWID();
DECLARE @ConvId3 UNIQUEIDENTIFIER = NEWID();

INSERT INTO Conversations (Id, TenantId, ChannelUserId, Channel, Status, FailedClassificationCount, CreatedAt, LastActivityAt)
VALUES
  (@ConvId1, @ExistingTenantId, 'user_123456', 'WhatsApp', 0, 0, GETUTCDATE(), GETUTCDATE()),
  (@ConvId2, @ExistingTenantId, 'user_789012', 'WhatsApp', 0, 0, GETUTCDATE(), GETUTCDATE());

-- Insert test incidents
-- IncidentCategory: Code=0, Database=1, Infrastructure=2, Other=3
-- IncidentSeverity: Low=0, Medium=1, High=2, Critical=3
-- IncidentStatus: Open=0, InProgress=1, PendingInfo=2, Resolved=3, Closed=4
INSERT INTO Incidents (Id, TenantId, ConversationId, Title, Description, Category, Severity, Status, AssignedTo, CreatedAt, UpdatedAt)
VALUES
  (NEWID(), @ExistingTenantId, @ConvId1, 'Database connection timeout', 'The application is experiencing timeout errors when connecting to the database during peak hours.', 1, 3, 0, 'admin', DATEADD(HOUR, -5, GETUTCDATE()), GETUTCDATE()),
  (NEWID(), @ExistingTenantId, @ConvId2, 'API response delay', 'Some API endpoints are responding slowly, affecting user experience.', 2, 2, 1, 'admin', DATEADD(HOUR, -2, GETUTCDATE()), GETUTCDATE()),
  (NEWID(), @ExistingTenantId, @ConvId1, 'Missing error handling', 'Error handling is missing in the payment processing module.', 0, 2, 0, 'admin', DATEADD(DAY, -1, GETUTCDATE()), GETUTCDATE()),
  (NEWID(), @ExistingTenantId, @ConvId2, 'Configuration issue', 'The tenant configuration is not being loaded correctly from the database.', 0, 1, 3, 'admin', DATEADD(DAY, -3, GETUTCDATE()), DATEADD(HOUR, -1, GETUTCDATE())),
  (NEWID(), @ExistingTenantId, @ConvId1, 'Security vulnerability', 'SQL injection vulnerability detected in the search functionality.', 0, 3, 0, 'admin', DATEADD(HOUR, -12, GETUTCDATE()), GETUTCDATE());

PRINT 'Seed data loaded successfully for Hive database.';
