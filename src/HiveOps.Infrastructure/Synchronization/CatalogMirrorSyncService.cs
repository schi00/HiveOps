using System.Data;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Models;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Infrastructure.Synchronization;

public sealed class CatalogMirrorSyncService : ICatalogMirrorSyncService
{
    private readonly AppDbContext _db;
    private readonly IEmbeddingService _embeddingService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CatalogMirrorSyncService> _logger;

    public CatalogMirrorSyncService(
        AppDbContext db,
        IEmbeddingService embeddingService,
        IHttpClientFactory httpClientFactory,
        ILogger<CatalogMirrorSyncService> logger)
    {
        _db = db;
        _embeddingService = embeddingService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CatalogSyncResult> SyncTenantAsync(Guid tenantId, CatalogSyncSettings settings, CancellationToken ct = default)
    {
        var result = new CatalogSyncResult();
        var rows = settings.SourceType switch
        {
            CatalogSyncSourceType.GoogleSheetsXlsx => await LoadFromGoogleSheetsAsync(settings, ct),
            CatalogSyncSourceType.SqlServer => await LoadFromSqlServerAsync(settings, ct),
            _ => []
        };

        result.Processed = rows.Count;
        if (rows.Count == 0)
            return result;

        var existingDefinitions = await _db.CatalogAttributeDefinitions
            .IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId)
            .ToDictionaryAsync(x => x.AttributeKey, StringComparer.OrdinalIgnoreCase, ct);

        var embeddings = await _embeddingService.GenerateEmbeddingsAsync(
            rows.Select(r => $"{r.Name} {r.Brand} {r.Description} {r.Tags}"), ct);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            try
            {
                Product? existing = null;
                if (!string.IsNullOrWhiteSpace(row.Sku))
                {
                    existing = await _db.Products
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Sku == row.Sku, ct);
                }

                existing ??= await _db.Products
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Name == row.Name && p.Brand == row.Brand, ct);

                if (existing is null)
                {
                    existing = new Product
                    {
                        TenantId = tenantId,
                        Name = row.Name,
                        Brand = row.Brand,
                        Category = row.Category,
                        Description = row.Description,
                        Tags = row.Tags,
                        Price = row.Price,
                        StockQuantity = row.StockQuantity,
                        Sku = row.Sku,
                        Embedding = embeddings[i]
                    };
                    _db.Products.Add(existing);
                    result.Inserted++;
                }
                else
                {
                    existing.Name = row.Name;
                    existing.Brand = row.Brand;
                    existing.Category = row.Category;
                    existing.Description = row.Description;
                    existing.Tags = row.Tags;
                    existing.Price = row.Price;
                    existing.StockQuantity = row.StockQuantity;
                    existing.Sku = row.Sku;
                    existing.Embedding = embeddings[i];
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                    result.Updated++;
                }

                await UpsertDynamicAttributesAsync(tenantId, existing, row.Attributes, existingDefinitions, ct);
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.Message);
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Tenant {TenantId} sync completed. Processed={Processed}, Inserted={Inserted}, Updated={Updated}, Errors={Errors}",
            tenantId, result.Processed, result.Inserted, result.Updated, result.Errors.Count);

        return result;
    }

    private async Task<List<ExternalProductRow>> LoadFromGoogleSheetsAsync(CatalogSyncSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.GoogleSheets.SpreadsheetUrl))
            return [];

        var url = BuildXlsxExportUrl(settings.GoogleSheets.SpreadsheetUrl);
        var client = _httpClientFactory.CreateClient(nameof(CatalogMirrorSyncService));
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var workbook = new XLWorkbook(stream);
        var worksheet = string.IsNullOrWhiteSpace(settings.GoogleSheets.SheetName)
            ? workbook.Worksheet(1)
            : workbook.Worksheet(settings.GoogleSheets.SheetName);

        return ReadRowsFromWorksheet(worksheet, settings.ColumnMap);
    }

    private static List<ExternalProductRow> ReadRowsFromWorksheet(IXLWorksheet worksheet, ProductColumnMap map)
    {
        var header = worksheet.FirstRowUsed();
        if (header is null) return [];

        var headerMap = header.CellsUsed()
            .ToDictionary(c => c.GetString().Trim(), c => c.Address.ColumnNumber, StringComparer.OrdinalIgnoreCase);

        var rows = new List<ExternalProductRow>();
        foreach (var row in worksheet.RowsUsed().Skip(1))
        {
            var name = GetCell(row, headerMap, map.Name);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            rows.Add(new ExternalProductRow
            {
                Name = name,
                Brand = GetCell(row, headerMap, map.Brand),
                Category = GetCell(row, headerMap, map.Category),
                Description = GetCell(row, headerMap, map.Description),
                Tags = GetCell(row, headerMap, map.Tags),
                Sku = GetCell(row, headerMap, map.Sku),
                Price = decimal.TryParse(GetCell(row, headerMap, map.Price), out var p) ? p : 0,
                StockQuantity = int.TryParse(GetCell(row, headerMap, map.StockQuantity), out var s) ? s : 0,
                Attributes = ReadDynamicAttributes(key => GetCell(row, headerMap, key), map)
            });
        }

        return rows;
    }

    private async Task<List<ExternalProductRow>> LoadFromSqlServerAsync(CatalogSyncSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.SqlServer.ConnectionString) || string.IsNullOrWhiteSpace(settings.SqlServer.Query))
            return [];

        var rows = new List<ExternalProductRow>();
        await using var conn = new SqlConnection(settings.SqlServer.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(settings.SqlServer.Query, conn)
        {
            CommandType = CommandType.Text,
            CommandTimeout = 60
        };

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var map = settings.ColumnMap;

        while (await reader.ReadAsync(ct))
        {
            var name = GetReaderValue(reader, map.Name);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            rows.Add(new ExternalProductRow
            {
                Name = name,
                Brand = GetReaderValue(reader, map.Brand),
                Category = GetReaderValue(reader, map.Category),
                Description = GetReaderValue(reader, map.Description),
                Tags = GetReaderValue(reader, map.Tags),
                Sku = GetReaderValue(reader, map.Sku),
                Price = decimal.TryParse(GetReaderValue(reader, map.Price), out var p) ? p : 0,
                StockQuantity = int.TryParse(GetReaderValue(reader, map.StockQuantity), out var s) ? s : 0,
                Attributes = ReadDynamicAttributes(key => GetReaderValue(reader, key), map)
            });
        }

        return rows;
    }

    private static string BuildXlsxExportUrl(string url)
    {
        if (url.Contains("/export?", StringComparison.OrdinalIgnoreCase))
            return url;

        if (url.Contains("/edit", StringComparison.OrdinalIgnoreCase))
            return url.Replace("/edit", "/export?format=xlsx", StringComparison.OrdinalIgnoreCase);

        return url;
    }

    private static string GetCell(IXLRow row, Dictionary<string, int> headerMap, string columnName)
    {
        if (!headerMap.TryGetValue(columnName, out var columnIndex))
            return string.Empty;

        return row.Cell(columnIndex).GetString().Trim();
    }

    private static string GetReaderValue(SqlDataReader reader, string columnName)
    {
        try
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal))?.Trim() ?? string.Empty;
        }
        catch (IndexOutOfRangeException)
        {
            return string.Empty;
        }
    }

    private async Task UpsertDynamicAttributesAsync(
        Guid tenantId,
        Product product,
        Dictionary<string, string> attributes,
        Dictionary<string, CatalogAttributeDefinition> definitionMap,
        CancellationToken ct)
    {
        if (attributes.Count == 0)
            return;

        var existingValues = await _db.ProductAttributeValues
            .IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId && x.ProductId == product.Id)
            .ToListAsync(ct);

        if (existingValues.Count > 0)
            _db.ProductAttributeValues.RemoveRange(existingValues);

        foreach (var (key, value) in attributes)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            if (!definitionMap.ContainsKey(key))
            {
                var definition = new CatalogAttributeDefinition
                {
                    TenantId = tenantId,
                    AttributeKey = key,
                    DisplayName = key,
                    DataType = "text",
                    IsFilterable = true,
                    IsSearchable = true,
                    SortOrder = definitionMap.Count
                };

                _db.CatalogAttributeDefinitions.Add(definition);
                definitionMap[key] = definition;
            }

            _db.ProductAttributeValues.Add(new ProductAttributeValue
            {
                TenantId = tenantId,
                ProductId = product.Id,
                AttributeKey = key,
                AttributeValue = value,
                NormalizedValue = NormalizeAttributeValue(value)
            });
        }
    }

    private static Dictionary<string, string> ReadDynamicAttributes(Func<string, string> valueAccessor, ProductColumnMap map)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (map.Attributes.Count == 0)
            return result;

        foreach (var kv in map.Attributes)
        {
            var attributeKey = kv.Key?.Trim();
            var columnName = kv.Value?.Trim();
            if (string.IsNullOrWhiteSpace(attributeKey) || string.IsNullOrWhiteSpace(columnName))
                continue;

            var value = valueAccessor(columnName).Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            result[attributeKey] = value;
        }

        return result;
    }

    private static string NormalizeAttributeValue(string value)
        => value.Trim().ToLowerInvariant();

    private sealed record ExternalProductRow
    {
        public string Name { get; init; } = string.Empty;
        public string Brand { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? Tags { get; init; }
        public string? Sku { get; init; }
        public decimal Price { get; init; }
        public int StockQuantity { get; init; }
        public Dictionary<string, string> Attributes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
