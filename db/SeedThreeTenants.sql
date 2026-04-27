SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- Force insert 3 tenants if not exist
INSERT INTO Tenants (Id, Name, Industry, ApiKey, IsActive, ConfigJson, CreatedAt)
SELECT NEWID(), 'Acme Corp', 'Technology', 'key_acme_' + CONVERT(VARCHAR(50), NEWID()), 1, '{"runtimeConfiguration":{"agent":{"model":"gpt-4","temperature":0.7,"maxTokens":2000}}}', GETUTCDATE()
WHERE NOT EXISTS (SELECT 1 FROM Tenants WHERE Name = 'Acme Corp');

INSERT INTO Tenants (Id, Name, Industry, ApiKey, IsActive, ConfigJson, CreatedAt)
SELECT NEWID(), 'Global Solutions', 'Finance', 'key_global_' + CONVERT(VARCHAR(50), NEWID()), 1, '{"runtimeConfiguration":{"agent":{"model":"gpt-3.5-turbo","temperature":0.5,"maxTokens":1000}}}', GETUTCDATE()
WHERE NOT EXISTS (SELECT 1 FROM Tenants WHERE Name = 'Global Solutions');

INSERT INTO Tenants (Id, Name, Industry, ApiKey, IsActive, ConfigJson, CreatedAt)
SELECT NEWID(), 'Contoso Retail', 'Retail', 'key_contoso_' + CONVERT(VARCHAR(50), NEWID()), 1, '{"runtimeConfiguration":{"agent":{"model":"gpt-4","temperature":0.6,"maxTokens":1500}}}', GETUTCDATE()
WHERE NOT EXISTS (SELECT 1 FROM Tenants WHERE Name = 'Contoso Retail');

-- Create tenant users for new tenants
INSERT INTO AppUsers (Id, Username, Email, Role, TenantId, PasswordHash, IsActive, CreatedAt, UpdatedAt)
SELECT NEWID(), CONCAT('tenant_', LOWER(REPLACE(Name,' ', '_'))), CONCAT('tenant_', LOWER(REPLACE(Name,' ', '_')), '@HiveOps.local'), 'Tenant', Id,
       'v1.100000.YWJjZGVmZ2hpamtsbW5vcA==.YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo=', 1, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
FROM Tenants t
WHERE NOT EXISTS (SELECT 1 FROM AppUsers u WHERE u.TenantId = t.Id AND u.Role = 'Tenant')
AND t.Name IN ('Acme Corp', 'Global Solutions', 'Contoso Retail');

PRINT 'Tenants and users created successfully';
