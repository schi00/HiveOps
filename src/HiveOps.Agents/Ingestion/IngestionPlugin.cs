using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Agents.Ingestion;

/// <summary>
/// IngestionPlugin — ETL agent for catalog ingestion.
/// Parses CSV/Excel, normalizes brand/category names, generates embeddings,
/// and upserts products into SQL Server.
/// Invoked exclusively by the CatalogIngestionWorker — not by the chat flow.
/// </summary>
public sealed class IngestionPlugin
{
    private readonly AppDbContext _db;
    private readonly IEmbeddingService _embeddingService;

    public IngestionPlugin(AppDbContext db, IEmbeddingService embeddingService)
    {
        _db = db;
        _embeddingService = embeddingService;
    }

    /// <summary>Process a CSV stream and upsert products for a specific tenant.</summary>
    public async Task<IngestionResult> ProcessCsvAsync(
        Guid tenantId, Stream csvStream, CancellationToken ct = default)
    {
        var records = ParseCsv(csvStream);
        var normalized = records.Select(r => NormalizeRecord(r)).ToList();

        var result = new IngestionResult();

        // Batch embed all descriptions to minimise API round-trips
        var texts = normalized.Select(r => $"{r.Name} {r.Brand} {r.Description} {r.Tags}").ToList();
        IReadOnlyList<float[]> embeddings;
        try
        {
            embeddings = await _embeddingService.GenerateEmbeddingsAsync(texts, ct);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Embedding generation failed: {ex.Message}");
            return result;
        }

        for (int i = 0; i < normalized.Count; i++)
        {
            try
            {
                var r = normalized[i];
                var existing = await _db.Products
                    .IgnoreQueryFilters() // bypass global filter: ingestion is system-level
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Sku == r.Sku, ct);

                if (existing is not null)
                {
                    existing.Name = r.Name;
                    existing.Brand = r.Brand;
                    existing.Category = r.Category;
                    existing.Description = r.Description;
                    existing.Tags = r.Tags;
                    existing.Price = r.Price;
                    existing.StockQuantity = r.StockQuantity;
                    existing.Embedding = embeddings[i];
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                    result.Updated++;
                }
                else
                {
                    _db.Products.Add(new Product
                    {
                        TenantId = tenantId,
                        Name = r.Name,
                        Brand = r.Brand,
                        Category = r.Category,
                        Description = r.Description,
                        Tags = r.Tags,
                        Price = r.Price,
                        StockQuantity = r.StockQuantity,
                        Sku = r.Sku,
                        Embedding = embeddings[i]
                    });
                    result.Inserted++;
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Row {i + 1}: {ex.Message}");
            }
        }

        await _db.SaveChangesAsync(ct);
        return result;
    }

    private static List<CatalogRecord> ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var records = new List<CatalogRecord>();

        // Skip header line
        var header = reader.ReadLine();
        if (header is null) return records;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var cols = line.Split(',');
            if (cols.Length < 6) continue;

            records.Add(new CatalogRecord
            {
                Name = cols[0].Trim(),
                Brand = cols[1].Trim(),
                Category = cols[2].Trim(),
                Description = cols[3].Trim(),
                Price = decimal.TryParse(cols[4].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 0,
                StockQuantity = int.TryParse(cols[5].Trim(), out var s) ? s : 0,
                Sku = cols.Length > 6 ? cols[6].Trim() : null,
                Tags = cols.Length > 7 ? cols[7].Trim() : null
            });
        }

        return records;
    }

    private static CatalogRecord NormalizeRecord(CatalogRecord r)
    {
        return r with
        {
            Brand = NormalizeBrand(r.Brand),
            Category = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(r.Category.ToLower()),
            Name = r.Name.Trim(),
            Description = r.Description.Trim()
        };
    }

    /// <summary>
    /// Normalizes common brand name variations.
    /// Extendable via configuration — covers the "Nike"/"nk"/"N1ke" problem described in the spec.
    /// </summary>
    private static string NormalizeBrand(string brand)
    {
        return brand.Trim().ToUpperInvariant() switch
        {
            "NK" or "NKE" or "N1KE" => "Nike",
            "ADS" or "ADID" => "Adidas",
            "PP" or "PUMA_" => "Puma",
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(brand.ToLower())
        };
    }

    private record CatalogRecord
    {
        public string Name { get; init; } = string.Empty;
        public string Brand { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public decimal Price { get; init; }
        public int StockQuantity { get; init; }
        public string? Sku { get; init; }
        public string? Tags { get; init; }
    }
}

public sealed class IngestionResult
{
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public List<string> Errors { get; } = [];
    public bool HasErrors => Errors.Count > 0;
}
