/* Adds billing, packaging and Stripe columns to Tenants table if missing */
SET NOCOUNT ON;
USE Hive;
GO

IF COL_LENGTH('dbo.Tenants','Plan') IS NULL
BEGIN
  ALTER TABLE dbo.Tenants ADD [Plan] int NOT NULL CONSTRAINT DF_Tenants_Plan DEFAULT(0);
END;

IF COL_LENGTH('dbo.Tenants','MaxMonthlyIncidents') IS NULL
BEGIN
  ALTER TABLE dbo.Tenants ADD [MaxMonthlyIncidents] int NULL;
END;

IF COL_LENGTH('dbo.Tenants','MaxUsers') IS NULL
BEGIN
  ALTER TABLE dbo.Tenants ADD [MaxUsers] int NULL;
END;

IF COL_LENGTH('dbo.Tenants','HasRollbackCapability') IS NULL
BEGIN
  ALTER TABLE dbo.Tenants ADD [HasRollbackCapability] bit NOT NULL CONSTRAINT DF_Tenants_HasRollbackCapability DEFAULT(1);
END;

IF COL_LENGTH('dbo.Tenants','StripeCustomerId') IS NULL
BEGIN
  ALTER TABLE dbo.Tenants ADD [StripeCustomerId] nvarchar(100) NULL;
END;

IF COL_LENGTH('dbo.Tenants','StripeSubscriptionId') IS NULL
BEGIN
  ALTER TABLE dbo.Tenants ADD [StripeSubscriptionId] nvarchar(100) NULL;
END;

IF COL_LENGTH('dbo.Tenants','StripePriceId') IS NULL
BEGIN
  ALTER TABLE dbo.Tenants ADD [StripePriceId] nvarchar(100) NULL;
END;

IF COL_LENGTH('dbo.Tenants','StripeSubscriptionItemId') IS NULL
BEGIN
  ALTER TABLE dbo.Tenants ADD [StripeSubscriptionItemId] nvarchar(100) NULL;
END;

IF COL_LENGTH('dbo.Tenants','SubscriptionStatus') IS NULL
BEGIN
  ALTER TABLE dbo.Tenants ADD [SubscriptionStatus] nvarchar(50) NULL;
END;

IF COL_LENGTH('dbo.Tenants','SubscriptionCurrentPeriodEnd') IS NULL
BEGIN
  ALTER TABLE dbo.Tenants ADD [SubscriptionCurrentPeriodEnd] datetimeoffset NULL;
END;

/* Ensure defaults are applied to existing rows if columns exist */
IF COL_LENGTH('dbo.Tenants','Plan') IS NOT NULL
BEGIN
  EXEC sp_executesql N'UPDATE dbo.Tenants SET [Plan] = ISNULL([Plan],0);';
END;
IF COL_LENGTH('dbo.Tenants','HasRollbackCapability') IS NOT NULL
BEGIN
  EXEC sp_executesql N'UPDATE dbo.Tenants SET [HasRollbackCapability] = ISNULL([HasRollbackCapability],1);';
END;
