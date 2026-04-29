-- Seed test data for HiveOps Dashboard
-- This script creates sample tenants and incidents for testing

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

-- Insert test tenants
DECLARE @TenantId1 UNIQUEIDENTIFIER = NEWID();
DECLARE @TenantId2 UNIQUEIDENTIFIER = NEWID();

INSERT INTO Tenants (Id, Name, Industry, ApiKey, IsActive, ConfigJson, CreatedAt)
VALUES 
  (@TenantId1, 'Acme Corp', 'Technology', 'key_' + CONVERT(VARCHAR(50), NEWID()), 1, 
   '{"runtimeConfiguration":{"agent":{"model":"gpt-4","temperature":0.7,"maxTokens":2000},"database":{"enabled":true,"connectionString":""}}}',
   GETUTCDATE()),
  (@TenantId2, 'Global Solutions', 'Finance', 'key_' + CONVERT(VARCHAR(50), NEWID()), 1,
   '{"runtimeConfiguration":{"agent":{"model":"gpt-3.5-turbo","temperature":0.5,"maxTokens":1000},"database":{"enabled":false,"connectionString":""}}}',
   GETUTCDATE());

-- Insert test conversations
DECLARE @ConvId1 UNIQUEIDENTIFIER = NEWID();
DECLARE @ConvId2 UNIQUEIDENTIFIER = NEWID();
DECLARE @ConvId3 UNIQUEIDENTIFIER = NEWID();

INSERT INTO Conversations (Id, TenantId, ChannelUserId, Channel, Status, FailedClassificationCount, CreatedAt, LastActivityAt)
VALUES
  (@ConvId1, @TenantId1, 'user_123456', 'WhatsApp', 0, 0, GETUTCDATE(), GETUTCDATE()),
  (@ConvId2, @TenantId1, 'user_789012', 'WhatsApp', 0, 0, GETUTCDATE(), GETUTCDATE()),
  (@ConvId3, @TenantId2, 'user_345678', 'WhatsApp', 0, 0, GETUTCDATE(), GETUTCDATE());

-- Insert test incidents
INSERT INTO Incidents (Id, TenantId, ConversationId, Title, Description, Category, Severity, Status, AssignedTo, CreatedAt, UpdatedAt)
VALUES
  (NEWID(), @TenantId1, @ConvId1, 'Database connection timeout', 'The application is experiencing timeout errors when connecting to the database during peak hours.', 'DbCorruption', 'Critical', 'Open', 'admin', DATEADD(HOUR, -5, GETUTCDATE()), GETUTCDATE()),
  (NEWID(), @TenantId1, @ConvId2, 'API response delay', 'Some API endpoints are responding slowly, affecting user experience.', 'Performance', 'High', 'InProgress', 'admin', DATEADD(HOUR, -2, GETUTCDATE()), GETUTCDATE()),
  (NEWID(), @TenantId1, @ConvId1, 'Missing error handling', 'Error handling is missing in the payment processing module.', 'CodeBug', 'High', 'Open', 'admin', DATEADD(DAY, -1, GETUTCDATE()), GETUTCDATE()),
  (NEWID(), @TenantId2, @ConvId3, 'Configuration issue', 'The tenant configuration is not being loaded correctly from the database.', 'ConfigError', 'Medium', 'Resolved', 'admin', DATEADD(DAY, -3, GETUTCDATE()), DATEADD(HOUR, -1, GETUTCDATE())),
  (NEWID(), @TenantId1, @ConvId1, 'Security vulnerability', 'SQL injection vulnerability detected in the search functionality.', 'Security', 'Critical', 'Open', 'admin', DATEADD(HOUR, -12, GETUTCDATE()), GETUTCDATE());

-- Display inserted data
SELECT 'Tenants inserted:' AS Info;
SELECT Id, Name, Industry, IsActive FROM Tenants WHERE Id IN (@TenantId1, @TenantId2);

SELECT 'Incidents inserted:' AS Info;
SELECT Id, TenantId, Title, Category, Severity, Status FROM Incidents WHERE TenantId IN (@TenantId1, @TenantId2);
