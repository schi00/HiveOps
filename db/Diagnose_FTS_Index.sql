-- Diagnostic script to verify Full-Text Search index status
-- Run this against your HiveOps database to ensure FTS is properly configured

PRINT '=== Full-Text Search Diagnostic Report ===';

-- 1. Check if Full-Text Search is installed
PRINT '';
PRINT '1. Checking FTS Installation:';
SELECT 
    FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') AS FTS_Installed,
    SERVERPROPERTY('ProductVersion') AS SQL_Version;

-- 2. Check FTS Catalog
PRINT '';
PRINT '2. Full-Text Search Catalogs:';
SELECT 
    name AS CatalogName,
    is_default AS IsDefault,
    is_importing AS IsImporting,
    is_accent_sensitivity_on AS AccentSensitive
FROM sys.fulltext_catalogs;

-- 3. Check FTS Indexes on Products table
PRINT '';
PRINT '3. Full-Text Indexes on Products Table:';
SELECT 
    ftix.object_id,
    OBJECT_NAME(ftix.object_id) AS TableName,
    ftix.unique_index_id,
    fc.name AS CatalogName,
    ftix.is_enabled
FROM sys.fulltext_indexes ftix
JOIN sys.fulltext_catalogs fc ON ftix.fulltext_catalog_id = fc.fulltext_catalog_id
WHERE OBJECT_NAME(ftix.object_id) = 'Products';

-- 4. Check FTS Index Columns
PRINT '';
PRINT '4. Full-Text Index Columns (Products):';
SELECT 
    ic.name AS ColumnName,
    ftc.language_id AS LanguageId,
    ftc.type_column_id AS TypeColumnId
FROM sys.fulltext_index_columns ftc
JOIN sys.columns ic ON ftc.object_id = ic.object_id AND ftc.column_id = ic.column_id
WHERE ftc.object_id = OBJECT_ID('Products');

-- 5. Check Products table row count and indexed rows
PRINT '';
PRINT '5. Products Table Statistics:';
SELECT 
    COUNT(*) AS TotalRows
FROM Products;

-- 6. Check if Product records have content in indexed columns
PRINT '';
PRINT '6. Products with FTS-Indexed Content (Sample):';
SELECT TOP 5
    Id,
    Name,
    Brand,
    Description,
    Tags,
    TenantId,
    DATALENGTH(Name) AS Name_Size,
    DATALENGTH(Brand) AS Brand_Size,
    DATALENGTH(Description) AS Description_Size
FROM Products
WHERE Name IS NOT NULL OR Brand IS NOT NULL
ORDER BY Id;

-- 7. Manually test FREETEXTTABLE (simple query)
PRINT '';
PRINT '7. Testing FREETEXTTABLE (searching for "adidas"):';
SELECT TOP 10
    FT_TBL.Id,
    FT_TBL.Name,
    FT_TBL.Brand,
    KEY_TBL.RANK
FROM Products AS FT_TBL
INNER JOIN FREETEXTTABLE(Products, (Name, Brand, Description, Tags), 'adidas', 100) AS KEY_TBL
    ON FT_TBL.Id = KEY_TBL.[KEY]
ORDER BY KEY_TBL.RANK DESC;

PRINT '';
PRINT '=== END OF DIAGNOSTIC REPORT ===';
PRINT '';
PRINT 'ACTION ITEMS:';
PRINT '- If FTS_Installed = 0: Enable Full-Text Search on SQL Server instance';
PRINT '- If no catalogs found: Run VadiSuite_Init.sql to create FTS infrastructure';
PRINT '- If test query returns 0 rows: Check if the tenant has product data';
PRINT '- If timeout still occurs with 120s limit: Consider adding index hint or optimizing the query';
