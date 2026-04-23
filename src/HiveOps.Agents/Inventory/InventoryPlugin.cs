using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Agents.Inventory;

/// <summary>
/// InventoryPlugin — Hybrid Search Expert.
/// Combines SQL Server vector search (VECTOR_DISTANCE) with Full-Text Search (CONTAINS)
/// using Reciprocal Rank Fusion to return the best ranked products.
/// Returns ONLY structured JSON data; formatting is delegated to the LLM planner.
/// </summary>
public sealed class InventoryPlugin
{
    private readonly IProductSearchService _searchService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IQueryNormalizer _queryNormalizer;
    private readonly AppDbContext _db;
    private readonly ILogger<InventoryPlugin> _logger;

    public InventoryPlugin(
        IProductSearchService searchService,
        IEmbeddingService embeddingService,
        AppDbContext db,
        ILogger<InventoryPlugin> logger,
        IQueryNormalizer? queryNormalizer = null)
    {
        _searchService = searchService;
        _embeddingService = embeddingService;
        _db = db;
        _logger = logger;
        _queryNormalizer = queryNormalizer ?? NoopQueryNormalizer.Instance;
    }

    [KernelFunction("search_products")]
    [Description("Searches for products using hybrid vector + full-text search. Returns a JSON list of matching products with stock and price information.")]
    public async Task<string> SearchProductsAsync(
        Kernel kernel,
        [Description("Natural language query describing what the customer is looking for.")] string query,
        [Description("Maximum number of results to return.")] int topK = 5,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        var originalQuery = query?.Trim() ?? string.Empty;
        var tenantContext = await BuildTenantContextAsync(tenantId, cancellationToken);
        var llmNormalized = await _queryNormalizer.NormalizeAsync(originalQuery, tenantContext, cancellationToken);
        var llmSearchTerm = BuildSearchTerm(llmNormalized);
        var effectiveQuery = !string.IsNullOrWhiteSpace(llmSearchTerm) ? llmSearchTerm : originalQuery;
        var requestedTopK = Math.Clamp(topK <= 0 ? 5 : topK, 1, 8);
        var normalizedQuery = await NormalizeQueryWithSynonymsAsync(tenantId, effectiveQuery, cancellationToken);
        normalizedQuery = ExpandIntentQuery(effectiveQuery, normalizedQuery);
        var sortByLowestPrice = IsLowestPriceIntent(normalizedQuery);
        var analysis = await AnalyzeQueryAsync(tenantId, normalizedQuery, cancellationToken);

        if (IsBrandListQuery(originalQuery) && !analysis.HasStructuredFilters)
            return await ListBrandsAsync(tenantId, cancellationToken);

        if (IsCategoryListQuery(originalQuery) && !analysis.HasStructuredFilters)
            return await ListProductTypesAsync(tenantId, cancellationToken);

        if (TryBuildNegativeAvailabilityResponse(originalQuery, out var negativeAvailabilityResponse))
            return negativeAvailabilityResponse;

        if (TryBuildGenericGuidanceResponse(originalQuery, normalizedQuery, analysis, out var guidanceResponse))
            return guidanceResponse;

        if (IsHomeTrainingIntent(normalizedQuery))
        {
            var homeTrainingResults = await SearchHomeTrainingProductsAsync(tenantId, requestedTopK, cancellationToken);
            if (homeTrainingResults.Count > 0)
            {
                return SerializeProductResults(homeTrainingResults, "home_training", originalQuery, analysis);
            }
        }

        var unsupportedSportResponse = await TryBuildUnsupportedSportResponseAsync(tenantId, normalizedQuery, cancellationToken);
        if (!string.IsNullOrWhiteSpace(unsupportedSportResponse))
            return unsupportedSportResponse;

        var structuredResults = await SearchStructuredCatalogAsync(tenantId, analysis, requestedTopK, sortByLowestPrice, cancellationToken);
        var conceptResults = await SearchByConceptMappingsAsync(tenantId, normalizedQuery, requestedTopK, sortByLowestPrice, cancellationToken);

        var deterministicResults = structuredResults
            .Concat(conceptResults)
            .GroupBy(r => r.ProductId)
            .Select(g => g.First())
            .OrderBy(r => sortByLowestPrice ? r.Price : 0)
            .ThenByDescending(r => sortByLowestPrice ? 0 : r.StockQuantity)
            .ThenBy(r => r.Price)
            .Take(requestedTopK)
            .ToList();

        if (deterministicResults.Count > 0)
            return SerializeProductResults(deterministicResults, "structured", originalQuery, analysis);

        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(normalizedQuery, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Generación de embedding falló para tenant {TenantId}. Usando búsqueda de texto.", tenantId);
            queryEmbedding = [];
        }

        var results = await _searchService.HybridSearchAsync(tenantId, normalizedQuery, queryEmbedding, requestedTopK, cancellationToken);

        if (results.Count > 0 && (analysis.Brand is not null || analysis.Category is not null || analysis.Color is not null || analysis.Size is not null))
        {
            var relatedResults = deterministicResults
                .Concat(await SearchStructuredCatalogAsync(tenantId, analysis, requestedTopK, sortByLowestPrice, cancellationToken))
                .Concat(await SearchByConceptMappingsAsync(tenantId, normalizedQuery, requestedTopK, sortByLowestPrice, cancellationToken))
                .GroupBy(r => r.ProductId)
                .Select(g => g.First())
                .ToList();
            results = relatedResults
                .Concat(results)
                .GroupBy(r => r.ProductId)
                .Select(g => g.First())
                .OrderBy(r => sortByLowestPrice ? r.Price : 0)
                .ThenByDescending(r => sortByLowestPrice ? 0 : r.StockQuantity)
                .ThenBy(r => r.Price)
                .Take(requestedTopK)
                .ToList();
        }

        if (sortByLowestPrice)
        {
            results = results
                .OrderBy(r => r.Price)
                .ThenByDescending(r => r.StockQuantity)
                .Take(requestedTopK)
                .ToList();
        }

        if (results.Count == 0)
        {
            var unsupportedAfterSearch = await TryBuildUnsupportedSportResponseAsync(tenantId, normalizedQuery, cancellationToken);
            if (!string.IsNullOrWhiteSpace(unsupportedAfterSearch))
                return unsupportedAfterSearch;

            if (TryBuildNegativeAvailabilityResponse(originalQuery, out var negativeAfterSearchResponse))
                return negativeAfterSearchResponse;

            return SerializeProductResults([], "empty", originalQuery, analysis);
        }

        return SerializeProductResults(results, "hybrid", originalQuery, analysis);
    }

    [KernelFunction("check_stock")]
    [Description("Returns the current stock level for a specific product by its ID.")]
    public async Task<string> CheckStockAsync(
        Kernel kernel,
        [Description("The product ID (GUID) to check stock for.")] string productId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);

        if (!Guid.TryParse(productId, out var pid))
            return "Invalid product ID format.";

        var match = await _db.Products
            .AsNoTracking()
            .Where(p => p.Id == pid)
            .Select(p => new { p.Id, p.StockQuantity })
            .FirstOrDefaultAsync(cancellationToken);

        return match is not null
            ? $"El producto tiene {match.StockQuantity} unidad(es) disponible(s){(match.StockQuantity == 0 ? " — sin stock en este momento" : "")}."
            : "No encontré ese producto en el catálogo.";
    }

    [KernelFunction("get_product_details")]
    [Description("Returns product details (name, brand, category, price, stock, sku) for a specific product by ID.")]
    public async Task<string> GetProductDetailsAsync(
        Kernel kernel,
        [Description("The product ID (GUID) to fetch.")] string productId,
        [Description("Whether variant-related hints should be included when available.")] bool includeVariants = true,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);

        if (!Guid.TryParse(productId, out var pid))
            return "Invalid product ID format.";

        var match = await _db.Products
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Id == pid)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Brand,
                p.Category,
                p.Price,
                p.StockQuantity,
                p.Sku,
                p.Description
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (match is null)
            return "No encontré ese producto en el catálogo.";

        var stockText = match.StockQuantity > 0 ? $"{match.StockQuantity} en stock" : "sin stock";
        var summary = $"{match.Name} - {match.Brand} - {match.Category} - ${match.Price:N0} - {stockText} - SKU {match.Sku ?? "N/A"}.";

        if (!includeVariants)
            return summary;

        var variants = await _db.ProductAttributeValues
            .AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.ProductId == pid)
            .OrderBy(v => v.AttributeKey)
            .ThenBy(v => v.AttributeValue)
            .Select(v => new { v.AttributeKey, v.AttributeValue })
            .Take(8)
            .ToListAsync(cancellationToken);

        if (variants.Count == 0)
            return summary;

        var variantText = string.Join(", ", variants.Select(v => $"{v.AttributeKey}: {v.AttributeValue}"));
        return $"{summary} Variantes: {variantText}.";
    }

    private static Guid GetTenantId(Kernel kernel)
    {
        if (kernel.Data.TryGetValue(KernelConstants.TenantIdKey, out var val) && val is Guid g && g != Guid.Empty)
            return g;

        throw new InvalidOperationException("TenantId is required for tenant-scoped plugin execution.");
    }

    private async Task<string> BuildTenantContextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Name, t.Industry })
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null)
            return "tenant_industry: general";

        var industry = string.IsNullOrWhiteSpace(tenant.Industry) ? "general" : tenant.Industry.Trim();
        return $"tenant_name: {tenant.Name}; tenant_industry: {industry}";
    }

    private static string BuildSearchTerm(QueryNormalizationResult normalized)
    {
        if (!normalized.IsSearchQuery)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(normalized.SearchTerm))
            return normalized.SearchTerm.Trim();

        if (normalized.Entities.Count == 0)
            return string.Empty;

        return string.Join(' ', normalized.Entities.Values.Where(v => !string.IsNullOrWhiteSpace(v))).Trim();
    }

    private sealed class NoopQueryNormalizer : IQueryNormalizer
    {
        public static IQueryNormalizer Instance { get; } = new NoopQueryNormalizer();

        public Task<QueryNormalizationResult> NormalizeAsync(string rawMessage, string tenantContext, CancellationToken cancellationToken = default)
            => Task.FromResult(QueryNormalizationResult.Empty);
    }

    private async Task<string> ListBrandsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var brands = await _db.Products
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && !string.IsNullOrWhiteSpace(p.Brand))
            .Select(p => p.Brand.Trim())
            .Distinct()
            .OrderBy(b => b)
            .Take(30)
            .ToListAsync(cancellationToken);

        if (brands.Count == 0)
            return SerializeListResponse("brands", []);

        var response = new
        {
            type = "brands",
            items = brands,
            count = brands.Count
        };
        return JsonSerializer.Serialize(response);
    }

    private async Task<string> ListProductTypesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var categories = await _db.Products
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && !string.IsNullOrWhiteSpace(p.Category))
            .Select(p => p.Category.Trim())
            .Distinct()
            .OrderBy(c => c)
            .Take(40)
            .ToListAsync(cancellationToken);

        if (categories.Count == 0)
            return SerializeListResponse("categories", []);

        var response = new
        {
            type = "categories",
            items = categories,
            count = categories.Count
        };
        return JsonSerializer.Serialize(response);
    }

    private static string SerializeListResponse(string type, List<string> items)
    {
        var response = new
        {
            type,
            items,
            count = items.Count
        };
        return JsonSerializer.Serialize(response);
    }

    private static string SerializeProductResults(
        IReadOnlyList<ProductSearchResult> results,
        string queryIntent,
        string originalQuery,
        SearchQueryAnalysis analysis)
    {
        var response = new
        {
            query_intent = queryIntent,
            results = results.Select(r => new
            {
                id = r.ProductId,
                name = r.Name,
                brand = r.Brand,
                price = r.Price,
                stock = r.StockQuantity,
                sku = r.Sku,
                description = r.Description
            }).ToArray(),
            result_count = results.Count,
            search_context = new
            {
                query = originalQuery,
                brand = analysis.Brand,
                category = analysis.Category,
                color = analysis.Color,
                size = analysis.Size
            }
        };
        return JsonSerializer.Serialize(response);
    }

    public static bool TryFormatProductResults(string json, out string formatted)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("query_intent", out _) ||
                !doc.RootElement.TryGetProperty("results", out var results))
            {
                formatted = json;
                return false;
            }

            var sb = new StringBuilder();
            var count = 0;
            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameProp) ||
                    !item.TryGetProperty("price", out var priceProp))
                    continue;

                var name = nameProp.GetString() ?? "";
                var brand = item.TryGetProperty("brand", out var b) ? b.GetString() : null;
                var price = priceProp.ValueKind == JsonValueKind.Number
                    ? priceProp.GetDouble()
                    : double.TryParse(priceProp.GetString(), out var pd) ? pd : 0;
                var stock = item.TryGetProperty("stock", out var s) ? s.GetInt32() : (int?)null;
                var sku = item.TryGetProperty("sku", out var sk) ? sk.GetString() : null;

                var line = $"• {name}";
                if (!string.IsNullOrWhiteSpace(brand))
                    line += $" – {brand}";
                line += $" – ${price:N0}";
                if (stock.HasValue)
                    line += $" – {stock.Value} en stock";
                if (!string.IsNullOrWhiteSpace(sku))
                    line += $" – SKU {sku}";
                sb.AppendLine(line);
                count++;
            }

            if (count == 0)
            {
                formatted = json;
                return false;
            }

            formatted = $"Encontré {count} opciones:\n\n{sb}";
            return true;
        }
        catch
        {
            formatted = json;
            return false;
        }
    }

    private static bool IsBrandListQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var text = query.ToLowerInvariant();
        return text.Contains("que marcas")
            || text.Contains("qué marcas")
            || text.Contains("marcas venden")
            || text.Contains("marcas manejan")
            || text.Contains("listado de marcas")
            || text.Contains("marcas trabajan");
    }

    private static bool IsCategoryListQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var text = query.ToLowerInvariant();
        return text.Contains("tipos de productos")
            || text.Contains("categorias")
            || text.Contains("categorías")
            || text.Contains("que productos venden")
            || text.Contains("qué productos venden")
            || text.Contains("que venden")
            || text.Contains("qué venden");
    }

    private async Task<SearchQueryAnalysis> AnalyzeQueryAsync(Guid tenantId, string query, CancellationToken cancellationToken)
    {
        var brands = await _db.Products
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && !string.IsNullOrWhiteSpace(p.Brand))
            .Select(p => p.Brand.Trim())
            .Distinct()
            .ToListAsync(cancellationToken);

        var categories = await _db.Products
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && !string.IsNullOrWhiteSpace(p.Category))
            .Select(p => p.Category.Trim())
            .Distinct()
            .ToListAsync(cancellationToken);

        var normalized = NormalizeText(query);
        var brand = FindCatalogMatch(brands, normalized);
        var category = FindCategoryMatch(categories, normalized);
        var color = FindColor(normalized);
        var size = FindSize(query, normalized);
        var keywords = ExtractMeaningfulTokens(normalized, brand, category, color, size);

        return new SearchQueryAnalysis(brand, category, color, size, keywords);
    }

    private async Task<List<ProductSearchResult>> SearchStructuredCatalogAsync(
        Guid tenantId,
        SearchQueryAnalysis analysis,
        int topK,
        bool sortByLowestPrice,
        CancellationToken cancellationToken)
    {
        IQueryable<Domain.Entities.Product> query = _db.Products
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(analysis.Brand))
        {
            var brandLike = $"%{analysis.Brand}%";
            query = query.Where(p => EF.Functions.Like(p.Brand, brandLike));
        }

        if (!string.IsNullOrWhiteSpace(analysis.Category))
        {
            var categoryLike = $"%{analysis.Category}%";
            query = query.Where(p => EF.Functions.Like(p.Category, categoryLike));
        }

        if (!string.IsNullOrWhiteSpace(analysis.Size))
        {
            var sizeLike = $"%{analysis.Size}%";
            var normalizedSizeLike = $"%{NormalizeText(analysis.Size)}%";
            query = query.Where(p =>
                EF.Functions.Like(p.Name, sizeLike) ||
                EF.Functions.Like(p.Description, sizeLike) ||
                (p.Tags != null && EF.Functions.Like(p.Tags, sizeLike)) ||
                (p.Sku != null && EF.Functions.Like(p.Sku, sizeLike)) ||
                _db.ProductAttributeValues.Any(v =>
                    v.TenantId == tenantId
                    && v.ProductId == p.Id
                    && (EF.Functions.Like(v.AttributeValue, sizeLike) || EF.Functions.Like(v.NormalizedValue, normalizedSizeLike))));
        }

        foreach (var keyword in analysis.Keywords.Take(4))
        {
            var keywordLike = $"%{keyword}%";
            var normalizedKeywordLike = $"%{NormalizeText(keyword)}%";
            query = query.Where(p =>
                EF.Functions.Like(p.Name, keywordLike) ||
                EF.Functions.Like(p.Brand, keywordLike) ||
                EF.Functions.Like(p.Category, keywordLike) ||
                EF.Functions.Like(p.Description, keywordLike) ||
                (p.Tags != null && EF.Functions.Like(p.Tags, keywordLike)) ||
                (p.Sku != null && EF.Functions.Like(p.Sku, keywordLike)) ||
                _db.ProductAttributeValues.Any(v =>
                    v.TenantId == tenantId
                    && v.ProductId == p.Id
                    && (EF.Functions.Like(v.AttributeValue, keywordLike) || EF.Functions.Like(v.NormalizedValue, normalizedKeywordLike))));
        }

        if (!analysis.HasStructuredFilters)
            return [];

        var rawResults = await (sortByLowestPrice
            ? query.OrderBy(p => p.Price).ThenByDescending(p => p.StockQuantity)
            : query.OrderByDescending(p => p.StockQuantity).ThenBy(p => p.Price))
            .Take(Math.Max(topK * 10, 50))
            .Select(p => new ProductSearchResult
            {
                ProductId = p.Id,
                Name = p.Name,
                Brand = p.Brand,
                Description = p.Description,
                Tags = p.Tags,
                Price = p.Price,
                StockQuantity = p.StockQuantity,
                Sku = p.Sku
            })
            .ToListAsync(cancellationToken);

        var attributeSearchIndex = await LoadAttributeSearchIndexAsync(tenantId, rawResults.Select(r => r.ProductId), cancellationToken);

        if (!string.IsNullOrWhiteSpace(analysis.Color))
        {
            var colorAliases = ExpandColorAliases(analysis.Color)
                .Select(NormalizeText)
                .ToList();

            rawResults = rawResults
                .Where(result => ContainsAnyAlias(result, colorAliases, attributeSearchIndex))
                .ToList();
        }

        return rawResults.Take(topK).ToList();
    }

    private async Task<List<ProductSearchResult>> SearchHomeTrainingProductsAsync(
        Guid tenantId,
        int topK,
        CancellationToken cancellationToken)
    {
        var homeTrainingTagLikes = new[]
        {
            "%,mancuerna,%",
            "%,pesas,%",
            "%,bandas,%",
            "%,colchoneta,%",
            "%,bicicleta,%",
            "%,cardio,%",
            "%,soga,%"
        };

        var results = await _db.Products
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && (
                EF.Functions.Like(p.Category, "%Equipamiento Gym%") ||
                EF.Functions.Like(p.Category, "%Equipamiento Cardio%") ||
                (p.Tags != null && homeTrainingTagLikes.Any(tagLike => EF.Functions.Like("," + p.Tags + ",", tagLike)))))
            .OrderByDescending(p => p.StockQuantity)
            .ThenBy(p => p.Price)
            .Take(Math.Max(topK * 2, 8))
            .Select(p => new ProductSearchResult
            {
                ProductId = p.Id,
                Name = p.Name,
                Brand = p.Brand,
                Description = p.Description,
                Tags = p.Tags,
                Price = p.Price,
                StockQuantity = p.StockQuantity,
                Sku = p.Sku
            })
            .ToListAsync(cancellationToken);

        return results.Take(topK).ToList();
    }

private static string? FindCatalogMatch(IEnumerable<string> catalogValues, string normalizedQuery)
    {
        return catalogValues
            .OrderByDescending(v => v.Length)
            .FirstOrDefault(v => normalizedQuery.Contains(NormalizeText(v), StringComparison.Ordinal));
    }

    private static string? FindCategoryMatch(IEnumerable<string> categories, string normalizedQuery)
    {
        var direct = FindCatalogMatch(categories, normalizedQuery);
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        foreach (var category in categories)
        {
            var aliases = GetCategoryAliases(category);
            if (aliases.Any(alias => normalizedQuery.Contains(alias, StringComparison.Ordinal)))
                return category;
        }

        return null;
    }

    private static IReadOnlyList<string> GetCategoryAliases(string category)
    {
        var normalizedCategory = NormalizeText(category);

        if (normalizedCategory.Contains("calzado", StringComparison.Ordinal))
            return ["zapa", "zapas", "zapatilla", "zapatillas", "running", "correr", "calzado", "tenis"];
        if (normalizedCategory.Contains("equipamiento gym", StringComparison.Ordinal) || normalizedCategory.Contains("gym", StringComparison.Ordinal))
            return ["gym", "gimnasio", "equipamiento", "mancuerna", "mancuernas", "pesas", "barra", "disco", "banda", "colchoneta"];
        if (normalizedCategory.Contains("equipamiento cardio", StringComparison.Ordinal) || normalizedCategory.Contains("cardio", StringComparison.Ordinal))
            return ["cardio", "bicicleta", "bici fija", "spinning", "remo", "soga", "entrenar en casa"];
        if (normalizedCategory.Contains("ropa", StringComparison.Ordinal) || normalizedCategory.Contains("indumentaria", StringComparison.Ordinal))
            return ["ropa", "remera", "remeras", "short", "shorts", "camiseta", "calza", "indumentaria"];
        if (normalizedCategory.Contains("accesorio", StringComparison.Ordinal))
            return ["accesorio", "accesorios", "gorra", "medias", "mochila", "bolso"];
        if (normalizedCategory.Contains("futbol", StringComparison.Ordinal))
            return ["futbol", "fútbol", "football", "footbol", "fulbo", "soccer", "pelota", "botines"];

        return normalizedCategory.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? FindColor(string normalizedQuery)
    {
        foreach (var colorGroup in ColorAliases)
        {
            if (colorGroup.Value.Any(alias => normalizedQuery.Contains(alias, StringComparison.Ordinal)))
                return colorGroup.Key;
        }

        return null;
    }

    private static IEnumerable<string> ExpandColorAliases(string color)
    {
        return ColorAliases.TryGetValue(color, out var aliases)
            ? aliases
            : [color];
    }

    private static string? FindSize(string originalQuery, string normalizedQuery)
    {
        var explicitMatch = Regex.Match(normalizedQuery, @"\b(?:talle|talla|size)\s*([a-z]{1,3}|\d{1,3})\b", RegexOptions.CultureInvariant);
        if (explicitMatch.Success)
            return explicitMatch.Groups[1].Value.ToUpperInvariant();

        var standaloneNumeric = Regex.Match(originalQuery, @"\b\d{2,3}\b", RegexOptions.CultureInvariant);
        if (standaloneNumeric.Success)
            return standaloneNumeric.Value;

        var textualSize = Regex.Match(normalizedQuery, @"\b(?:xs|s|m|l|xl|xxl)\b", RegexOptions.CultureInvariant);
        return textualSize.Success ? textualSize.Value.ToUpperInvariant() : null;
    }

    private async Task<string> NormalizeQueryWithSynonymsAsync(Guid tenantId, string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var mappings = await _db.Synonyms
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => new { s.Term, s.Normalized })
            .ToListAsync(cancellationToken);

        if (mappings.Count == 0)
            return NormalizeText(query.Trim());

        var normalizedQuery = NormalizeText(query.Trim());
        var normalizedMappings = mappings
            .Select(m => new { Term = NormalizeText(m.Term), Normalized = NormalizeText(m.Normalized) })
            .Where(m => !string.IsNullOrWhiteSpace(m.Term) && !string.IsNullOrWhiteSpace(m.Normalized))
            .GroupBy(m => m.Term)
            .Select(g => g.First())
            .OrderByDescending(m => m.Term.Length)
            .ToList();

        foreach (var mapping in normalizedMappings)
        {
            var escapedTerm = Regex.Escape(mapping.Term);
            normalizedQuery = Regex.Replace(
                normalizedQuery,
                $@"(?<![a-z0-9]){escapedTerm}(?![a-z0-9])",
                mapping.Normalized,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        return Regex.Replace(normalizedQuery, @"\s+", " ").Trim();
    }

    private async Task<List<ProductSearchResult>> SearchByConceptMappingsAsync(
        Guid tenantId,
        string normalizedQuery,
        int topK,
        bool sortByLowestPrice,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return [];

        var concepts = await _db.Concepts
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(cancellationToken);

        var matchingConceptIds = concepts
            .Where(c => normalizedQuery.Contains(NormalizeText(c.Name), StringComparison.Ordinal))
            .Select(c => c.Id)
            .ToList();

        if (matchingConceptIds.Count == 0)
            return [];

        var maps = await _db.ConceptProductMaps
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && matchingConceptIds.Contains(m.ConceptId))
            .OrderBy(m => m.Priority)
            .Select(m => new { m.Category, m.Tags })
            .ToListAsync(cancellationToken);

        var categories = maps
            .Select(m => m.Category?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tags = maps
            .SelectMany(m => (m.Tags ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (categories.Count == 0 && tags.Count == 0)
            return [];

        IQueryable<Guid>? mappedProductIdsQuery = null;

        foreach (var category in categories)
        {
            var categoryLike = $"%{category}%";
            var branchIds = _db.Products
                .AsNoTracking()
                .Where(p => p.TenantId == tenantId && EF.Functions.Like(p.Category, categoryLike))
                .Select(p => p.Id);

            mappedProductIdsQuery = mappedProductIdsQuery is null ? branchIds : mappedProductIdsQuery.Union(branchIds);
        }

        foreach (var tag in tags)
        {
            var normalizedTag = NormalizeText(tag);
            var tagLike = $"%,{normalizedTag},%";
            var branchIds = _db.Products
                .AsNoTracking()
                .Where(p => p.TenantId == tenantId && p.Tags != null && EF.Functions.Like("," + p.Tags + ",", tagLike))
                .Select(p => p.Id);

            mappedProductIdsQuery = mappedProductIdsQuery is null ? branchIds : mappedProductIdsQuery.Union(branchIds);
        }

        if (mappedProductIdsQuery is null)
            return [];

        var mappedProductIds = await mappedProductIdsQuery
            .Distinct()
            .Take(Math.Max(topK * 12, 100))
            .ToListAsync(cancellationToken);

        if (mappedProductIds.Count == 0)
            return [];

        var rawResults = await _db.Products
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && mappedProductIds.Contains(p.Id))
            .OrderBy(p => sortByLowestPrice ? p.Price : decimal.Zero)
            .ThenByDescending(p => sortByLowestPrice ? 0 : p.StockQuantity)
            .ThenBy(p => p.Price)
            .Take(Math.Max(topK * 6, 30))
            .Select(p => new ProductSearchResult
            {
                ProductId = p.Id,
                Name = p.Name,
                Brand = p.Brand,
                Description = p.Description,
                Tags = p.Tags,
                Price = p.Price,
                StockQuantity = p.StockQuantity,
                Sku = p.Sku
            })
            .ToListAsync(cancellationToken);

        return rawResults
            .GroupBy(r => r.ProductId)
            .Select(g => g.First())
            .Take(topK)
            .ToList();
    }

    private static List<string> ExtractMeaningfulTokens(
        string normalizedQuery,
        string? brand,
        string? category,
        string? color,
        string? size)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "que", "qué", "tienen", "tiene", "tenes", "hay", "quiero", "busco", "buscar", "de", "del", "la", "las", "el", "los",
            "un", "una", "unos", "unas", "para", "con", "por", "me", "mostrar", "mostrame", "muestrame", "producto", "productos",
            "marca", "marcas", "color", "colores", "talle", "talles", "talla", "categorias", "categorías", "categoria", "categoría",
            "venden", "vende", "manejan", "ver", "opciones", "del", "misma", "listado", "trabajan", "algo", "hacer",
            "lo", "mas", "más", "barato", "economico", "económico", "menor", "precio", "bajo"
        };

        var ignoredValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(brand))
            ignoredValues.Add(NormalizeText(brand));
        if (!string.IsNullOrWhiteSpace(category))
            ignoredValues.UnionWith(GetCategoryAliases(category));
        if (!string.IsNullOrWhiteSpace(color))
            ignoredValues.UnionWith(ExpandColorAliases(color));
        if (!string.IsNullOrWhiteSpace(size))
            ignoredValues.Add(size);

        return normalizedQuery
            .Split([' ', ',', '.', ';', ':', '-', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim().Trim('?', '!', '"', '\''))
            .Select(token => token.EndsWith('s') && token.Length > 4 ? token[..^1] : token)
            .Where(token => !stopWords.Contains(token))
            .Where(token => !ignoredValues.Contains(token))
            .Where(token => token.Length >= 3 || token.All(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string ExpandIntentQuery(string originalQuery, string normalizedQuery)
    {
        var normalizedOriginal = NormalizeText(originalQuery);

        if (ContainsAny(normalizedOriginal, "empezar a correr", "arrancar a correr", "salir a correr", "quiero correr"))
            return "zapatillas running";

        return normalizedQuery;
    }

    private static bool IsLowestPriceIntent(string normalizedQuery)
        => ContainsAny(normalizedQuery,
            "lo mas barato",
            "más barato",
            "mas barato",
            "mas economico",
            "más economico",
            "más económico",
            "economico",
            "economico",
            "menor precio",
            "precio mas bajo",
            "precio más bajo",
            "barato");

    private static bool TryBuildGenericGuidanceResponse(
        string originalQuery,
        string normalizedQuery,
        SearchQueryAnalysis analysis,
        out string response)
    {
        response = string.Empty;

        if (analysis.Brand is not null || analysis.Category is not null || analysis.Color is not null || analysis.Size is not null)
            return false;

        if (analysis.Keywords.Any(IsConcreteSearchKeyword))
            return false;

        if (!(ContainsAny(normalizedQuery, "algo", "deporte", "deportes", "articulos deportivos", "hacer deporte")
            || originalQuery.StartsWith("algo para", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (Regex.IsMatch(normalizedQuery, @"\bpara\s+(?!(?:hacer\s+)?deporte)\w{3,}"))
            return false;

        response = SerializeProductResults([], "generic_guidance", originalQuery, analysis);
        return true;
    }

    private static bool IsHomeTrainingIntent(string normalizedQuery)
        => ContainsAny(normalizedQuery,
            "entrenar en casa",
            "training en casa",
            "hacer ejercicio en casa",
            "ejercicio en casa",
            "home gym");

    private async Task<string?> TryBuildUnsupportedSportResponseAsync(
        Guid tenantId,
        string normalizedQuery,
        CancellationToken cancellationToken)
    {
        var requestedSport = KnownUnsupportedSports
            .FirstOrDefault(pair => pair.Value.Any(alias => normalizedQuery.Contains(alias, StringComparison.Ordinal)))
            .Key;

        if (string.IsNullOrWhiteSpace(requestedSport))
            return null;

        var supportedConcepts = await _db.Concepts
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .Select(c => NormalizeText(c.Name))
            .ToListAsync(cancellationToken);

        if (supportedConcepts.Contains(requestedSport, StringComparer.OrdinalIgnoreCase))
            return null;

        var response = new
        {
            query_intent = "unsupported_sport",
            results = new object[0],
            result_count = 0,
            search_context = new
            {
                query = requestedSport,
                unsupported_sport = requestedSport
            }
        };
        return JsonSerializer.Serialize(response);
    }

    private static bool TryBuildNegativeAvailabilityResponse(string originalQuery, out string response)
    {
        response = string.Empty;

        if (string.IsNullOrWhiteSpace(originalQuery))
            return false;

        var normalized = NormalizeText(originalQuery);
        var hasNegativeStatement =
            normalized.Contains("no hay", StringComparison.Ordinal)
            || normalized.Contains("ahi no hay", StringComparison.Ordinal)
            || normalized.Contains("aca no hay", StringComparison.Ordinal)
            || normalized.Contains("no tienen", StringComparison.Ordinal)
            || normalized.Contains("no tenes", StringComparison.Ordinal)
            || normalized.Contains("no tene", StringComparison.Ordinal);

        if (!hasNegativeStatement)
            return false;

        if (TryExtractNegativeTopic(originalQuery, normalized, out var topic))
        {
            var analysis = new SearchQueryAnalysis(null, null, null, null, []);
            response = SerializeProductResults([], "negative_availability", topic, analysis);
            return true;
        }

        return false;
    }

    private static bool TryExtractNegativeTopic(string originalQuery, string normalizedQuery, out string topic)
    {
        topic = string.Empty;

        var patterns = new[]
        {
            @"\b(?:cosas|articulos|productos)\s+de\s+([a-záéíóúñ\s]{3,})$",
            @"\bde\s+([a-záéíóúñ\s]{3,})$"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(originalQuery, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var raw = match.Groups[1].Value.Trim().Trim('.', ',', ';', ':', '!', '?');
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            topic = raw.ToLowerInvariant();
            return true;
        }

        if (normalizedQuery.Contains("natacion", StringComparison.Ordinal))
        {
            topic = "natación";
            return true;
        }

        return false;
    }

    private static bool ContainsAny(string text, params string[] tokens)
        => tokens.Any(text.Contains);

    private static bool IsConcreteSearchKeyword(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return false;

        return keyword switch
        {
            "deporte" or "deportes" or "deportivo" or "deportivos" or "articulo" or "articulos" or "producto" or "productos" or "categoria" or "categorias" => false,
            _ => true
        };
    }

    private static bool ContainsAnyAlias(ProductSearchResult result, IReadOnlyCollection<string> aliases)
    {
        var searchableText = NormalizeText($"{result.Name} {result.Description} {result.Tags} {result.Sku}");
        return aliases.Any(alias => searchableText.Contains(alias, StringComparison.Ordinal));
    }

    private static bool ContainsAnyAlias(
        ProductSearchResult result,
        IReadOnlyCollection<string> aliases,
        IReadOnlyDictionary<Guid, string> attributeSearchIndex)
    {
        var attributeText = attributeSearchIndex.TryGetValue(result.ProductId, out var value) ? value : string.Empty;
        var searchableText = NormalizeText($"{result.Name} {result.Description} {result.Tags} {result.Sku} {attributeText}");
        return aliases.Any(alias => searchableText.Contains(alias, StringComparison.Ordinal));
    }

    private async Task<Dictionary<Guid, string>> LoadAttributeSearchIndexAsync(
        Guid tenantId,
        IEnumerable<Guid> productIds,
        CancellationToken cancellationToken)
    {
        var ids = productIds.Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var values = await _db.ProductAttributeValues
            .AsNoTracking()
            .Where(v => v.TenantId == tenantId && ids.Contains(v.ProductId))
            .Select(v => new { v.ProductId, v.AttributeValue, v.NormalizedValue })
            .ToListAsync(cancellationToken);

        return values
            .GroupBy(v => v.ProductId)
            .ToDictionary(
                g => g.Key,
                g => string.Join(' ', g.Select(x => $"{x.AttributeValue} {x.NormalizedValue}")).Trim());
    }

    private static readonly Dictionary<string, string[]> ColorAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["negro"] = ["negro", "negra", "negros", "negras"],
        ["blanco"] = ["blanco", "blanca", "blancos", "blancas"],
        ["azul"] = ["azul", "azules"],
        ["rojo"] = ["rojo", "roja", "rojos", "rojas"],
        ["verde"] = ["verde", "verdes"],
        ["gris"] = ["gris", "grises"],
        ["rosa"] = ["rosa", "rosado", "rosada", "rosas"],
        ["marron"] = ["marron", "marrón", "marrones", "beige"]
    };

    private static readonly Dictionary<string, string[]> KnownUnsupportedSports = new(StringComparer.OrdinalIgnoreCase)
    {
        ["basquet"] = ["basquet", "basket", "basketball", "basquetbol"],
        ["voleibol"] = ["voley", "volley", "voleibol", "volleyball"],
        ["rugby"] = ["rugby"],
        ["hockey"] = ["hockey", "hockey cesped", "hockey hielo"]
    };

    private sealed record SearchQueryAnalysis(
        string? Brand,
        string? Category,
        string? Color,
        string? Size,
        IReadOnlyList<string> Keywords)
    {
        public bool HasStructuredFilters =>
            !string.IsNullOrWhiteSpace(Brand)
            || !string.IsNullOrWhiteSpace(Category)
            || !string.IsNullOrWhiteSpace(Color)
            || !string.IsNullOrWhiteSpace(Size)
            || Keywords.Count > 0;
    }
}
