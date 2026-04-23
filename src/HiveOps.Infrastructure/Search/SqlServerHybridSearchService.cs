using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HiveOps.Infrastructure.Search;

/// <summary>
/// Hybrid search implementation for SQL Server 2022.
/// Combines:
///   - VECTOR_DISTANCE (cosine) for semantic / concept search
///   - CONTAINS (Full-Text Search) for exact brand/name matches
/// Results are merged using Reciprocal Rank Fusion (RRF).
/// </summary>
public sealed class SqlServerHybridSearchService : IProductSearchService
{
    private readonly string _connectionString;
    private readonly ILogger<SqlServerHybridSearchService> _logger;
    private const int RrfK = 60; // Standard RRF constant
    private const int FtsQueryTimeoutSeconds = 4;

    public SqlServerHybridSearchService(string connectionString, ILogger<SqlServerHybridSearchService> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ProductSearchResult>> HybridSearchAsync(
        Guid tenantId,
        string query,
        float[] queryEmbedding,
        int topK = 10,
        CancellationToken ct = default)
    {
        var filters = ParseFilters(query);

        var vectorResults = queryEmbedding.Length == 0
            ? new List<ProductSearchResult>()
            : await VectorSearchAsync(tenantId, queryEmbedding, topK * 2, ct);

        var ftsResults = string.IsNullOrWhiteSpace(query)
            ? new List<ProductSearchResult>()
            : await FullTextSearchAsync(tenantId, query, filters, topK * 2, ct);

        if (ftsResults.Count == 0 && !string.IsNullOrWhiteSpace(query))
        {
            _logger.LogInformation("FTS search returned no rows for tenant {TenantId}. Falling back to keyword search.", tenantId);
            ftsResults = await KeywordSearchAsync(tenantId, query, filters, topK * 2, ct);
        }

        return MergeWithRrf(vectorResults, ftsResults, topK);
    }

    private async Task<List<ProductSearchResult>> VectorSearchAsync(
        Guid tenantId, float[] embedding, int topK, CancellationToken ct)
    {
        var results = new List<ProductSearchResult>();

        // SQL Server 2022 VECTOR_DISTANCE syntax
        const string sql = """
            SELECT TOP (@topK)
                Id, Name, Brand, Description, Price, StockQuantity, Sku,
                                VECTOR_DISTANCE('cosine', Embedding, CAST(@embedding AS VECTOR(1536))) AS VectorScore
            FROM Products
            WHERE TenantId = @tenantId
              AND Embedding IS NOT NULL
            ORDER BY VectorScore ASC
            """;

        await using var conn = await OpenTenantConnectionAsync(tenantId, ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = 120;
        cmd.Parameters.AddWithValue("@topK", topK);
        cmd.Parameters.AddWithValue("@tenantId", tenantId);
        // Build JSON vector using invariant culture to avoid decimal-comma locale issues.
        cmd.Parameters.AddWithValue("@embedding", BuildVectorLiteral(embedding));

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new ProductSearchResult
                {
                    ProductId = reader.GetGuid(0),
                    Name = reader.GetString(1),
                    Brand = reader.GetString(2),
                    Description = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Price = reader.GetDecimal(4),
                    StockQuantity = reader.GetInt32(5),
                    Sku = reader.IsDBNull(6) ? null : reader.GetString(6),
                    VectorScore = reader.GetDouble(7)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vector search failed for tenant {TenantId}", tenantId);
        }

        return results;
    }

    private async Task<List<ProductSearchResult>> KeywordSearchAsync(
        Guid tenantId, string query, SearchFilters filters, int topK, CancellationToken ct)
    {
        var tokens = ExtractSearchTokens(query);
        if (tokens.Count == 0)
            return [];

        var results = new List<ProductSearchResult>();
        var sql = new StringBuilder(
            """
            SELECT TOP (@topK)
                Id,
                Name,
                Brand,
                Description,
                Price,
                StockQuantity,
                Sku,
                (
            """);

        for (var i = 0; i < tokens.Count; i++)
        {
            if (i > 0)
                sql.Append(" + ");

            sql.Append($"CASE WHEN Name LIKE @term{i} THEN 30 ELSE 0 END + ");
            sql.Append($"CASE WHEN Brand LIKE @term{i} THEN 20 ELSE 0 END + ");
            sql.Append($"CASE WHEN Category LIKE @term{i} THEN 12 ELSE 0 END + ");
            sql.Append($"CASE WHEN Tags LIKE @term{i} THEN 10 ELSE 0 END + ");
            sql.Append($"CASE WHEN Description LIKE @term{i} THEN 6 ELSE 0 END");
        }

        sql.Append(
            """
                ) AS KeywordScore
                        FROM Products AS P
                        WHERE P.TenantId = @tenantId
                            AND (@brand IS NULL OR P.Brand LIKE @brandLike)
                            AND (@category IS NULL OR P.Category LIKE @categoryLike)
                            AND (@minPrice IS NULL OR P.Price >= @minPrice)
                            AND (@maxPrice IS NULL OR P.Price <= @maxPrice)
                            AND (@inStockOnly = 0 OR P.StockQuantity > 0)
              AND (
            """);

        for (var i = 0; i < tokens.Count; i++)
        {
            if (i > 0)
                sql.Append(" OR ");

            sql.Append($"P.Name LIKE @term{i} OR P.Brand LIKE @term{i} OR P.Category LIKE @term{i} OR P.Tags LIKE @term{i} OR P.Description LIKE @term{i}");
        }

        sql.Append(
            """
              )
            ORDER BY KeywordScore DESC, StockQuantity DESC, Price ASC
            """);

        await using var conn = await OpenTenantConnectionAsync(tenantId, ct);

        await using var cmd = new SqlCommand(sql.ToString(), conn);
        cmd.CommandTimeout = 120;
        cmd.Parameters.AddWithValue("@topK", topK);
        cmd.Parameters.AddWithValue("@tenantId", tenantId);
        cmd.Parameters.AddWithValue("@brand", (object?)filters.Brand ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@brandLike", filters.Brand is null ? DBNull.Value : $"%{filters.Brand}%");
        cmd.Parameters.AddWithValue("@category", (object?)filters.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@categoryLike", filters.Category is null ? DBNull.Value : $"%{filters.Category}%");
        cmd.Parameters.AddWithValue("@minPrice", (object?)filters.MinPrice ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@maxPrice", (object?)filters.MaxPrice ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@inStockOnly", filters.InStockOnly ? 1 : 0);

        for (var i = 0; i < tokens.Count; i++)
            cmd.Parameters.AddWithValue($"@term{i}", $"%{tokens[i]}%");

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new ProductSearchResult
                {
                    ProductId = reader.GetGuid(0),
                    Name = reader.GetString(1),
                    Brand = reader.GetString(2),
                    Description = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Price = reader.GetDecimal(4),
                    StockQuantity = reader.GetInt32(5),
                    Sku = reader.IsDBNull(6) ? null : reader.GetString(6),
                    FtsScore = Convert.ToDouble(reader.GetValue(7))
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Keyword search failed for tenant {TenantId}", tenantId);
        }

        return results;
    }

    private async Task<List<ProductSearchResult>> FullTextSearchAsync(
        Guid tenantId, string query, SearchFilters filters, int topK, CancellationToken ct)
    {
        var results = new List<ProductSearchResult>();

        // Sanitize query for FTS — remove SQL special chars
        var sanitized = SanitizeFtsQuery(query);
        if (string.IsNullOrWhiteSpace(sanitized)) return results;

        // FREETEXTTABLE: búsqueda semántica flexible con stemming y word-breaking
        // más robusta que CONTAINSTABLE para consultas en lenguaje natural español
        const string sql = """
            SELECT TOP (@topK)
                FT_TBL.Id, FT_TBL.Name, FT_TBL.Brand, FT_TBL.Description,
                FT_TBL.Price, FT_TBL.StockQuantity, FT_TBL.Sku,
                KEY_TBL.RANK AS FtsScore
            FROM Products AS FT_TBL
            INNER JOIN FREETEXTTABLE(Products, (Name, Brand, Description, Tags), @query, @topK) AS KEY_TBL
                ON FT_TBL.Id = KEY_TBL.[KEY]
            WHERE FT_TBL.TenantId = @tenantId
                            AND (@brand IS NULL OR FT_TBL.Brand LIKE @brandLike)
                            AND (@category IS NULL OR FT_TBL.Category LIKE @categoryLike)
                            AND (@minPrice IS NULL OR FT_TBL.Price >= @minPrice)
                            AND (@maxPrice IS NULL OR FT_TBL.Price <= @maxPrice)
                            AND (@inStockOnly = 0 OR FT_TBL.StockQuantity > 0)
            ORDER BY FtsScore DESC
            """;

        await using var conn = await OpenTenantConnectionAsync(tenantId, ct);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = FtsQueryTimeoutSeconds;
        cmd.Parameters.AddWithValue("@topK", topK);
        cmd.Parameters.AddWithValue("@tenantId", tenantId);
        cmd.Parameters.AddWithValue("@query", sanitized);
        cmd.Parameters.AddWithValue("@brand", (object?)filters.Brand ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@brandLike", filters.Brand is null ? DBNull.Value : $"%{filters.Brand}%");
        cmd.Parameters.AddWithValue("@category", (object?)filters.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@categoryLike", filters.Category is null ? DBNull.Value : $"%{filters.Category}%");
        cmd.Parameters.AddWithValue("@minPrice", (object?)filters.MinPrice ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@maxPrice", (object?)filters.MaxPrice ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@inStockOnly", filters.InStockOnly ? 1 : 0);

        using var ftsTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ftsTimeoutCts.CancelAfter(TimeSpan.FromSeconds(FtsQueryTimeoutSeconds));

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ftsTimeoutCts.Token);
            while (await reader.ReadAsync(ftsTimeoutCts.Token))
            {
                results.Add(new ProductSearchResult
                {
                    ProductId = reader.GetGuid(0),
                    Name = reader.GetString(1),
                    Brand = reader.GetString(2),
                    Description = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Price = reader.GetDecimal(4),
                    StockQuantity = reader.GetInt32(5),
                    Sku = reader.IsDBNull(6) ? null : reader.GetString(6),
                    FtsScore = Convert.ToDouble(reader.GetValue(7))
                });
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogInformation(
                "FTS query timed out after {TimeoutSeconds}s for tenant {TenantId}. Falling back to keyword search.",
                FtsQueryTimeoutSeconds,
                tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FTS search failed for tenant {TenantId}", tenantId);
        }

        return results;
    }

    /// <summary>
    /// Reciprocal Rank Fusion merges two ranked lists into one without score normalization.
    /// Score(d) = Σ 1/(k + rank(d))  where k=60 reduces the impact of highly-ranked outliers.
    /// </summary>
    private static IReadOnlyList<ProductSearchResult> MergeWithRrf(
        IReadOnlyList<ProductSearchResult> vectorList,
        IReadOnlyList<ProductSearchResult> ftsList,
        int topK)
    {
        var scores = new Dictionary<Guid, (ProductSearchResult Product, double Score)>();

        for (int i = 0; i < vectorList.Count; i++)
        {
            var item = vectorList[i];
            var rrf = 1.0 / (RrfK + i + 1);
            if (scores.TryGetValue(item.ProductId, out var entry))
                scores[item.ProductId] = (entry.Product, entry.Score + rrf);
            else
                scores[item.ProductId] = (item, rrf);
        }

        for (int i = 0; i < ftsList.Count; i++)
        {
            var item = ftsList[i];
            var rrf = 1.0 / (RrfK + i + 1);
            if (scores.TryGetValue(item.ProductId, out var entry))
                scores[item.ProductId] = (entry.Product, entry.Score + rrf);
            else
                scores[item.ProductId] = (item, rrf);
        }

        return scores.Values
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Product with { CombinedScore = x.Score })
            .ToList();
    }

    private static string SanitizeFtsQuery(string query)
    {
        // Remove characters that have meaning in SQL FTS syntax
        var chars = new[] { '"', '\'', '*', '(', ')', '&', '|', '!', '~', ',', ';', '-' };
        foreach (var c in chars)
            query = query.Replace(c.ToString(), " ");
        return query.Trim();
    }

    private static List<string> ExtractSearchTokens(string query)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tienen", "tiene", "tenes", "hay", "quiero", "busco", "buscar", "de", "del", "la", "las", "el", "los",
            "un", "una", "unos", "unas", "para", "con", "por", "me", "mostrar", "mostrame", "muestrame",
            "hasta", "desde", "maximo", "minimo", "stock", "disponible", "disponibles", "precio", "talle", "talla", "size"
        };

        return query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim().Trim('"', '\'', '.', ',', ';', ':', '!', '?'))
            .Select(token => token.EndsWith('s') && token.Length > 4 ? token[..^1] : token)
            .Where(token => (token.Length >= 3 || token.All(char.IsDigit)) && !stopWords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static SearchFilters ParseFilters(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return SearchFilters.Empty;

        var text = query.ToLowerInvariant();

        string? brand = null;
        var knownBrands = new[]
        {
            "nike", "adidas", "puma", "under armour", "jbl", "domyos", "garmin", "wilson", "head", "everlast"
        };
        foreach (var knownBrand in knownBrands)
        {
            if (!text.Contains(knownBrand, StringComparison.Ordinal))
                continue;

            brand = knownBrand switch
            {
                "under armour" => "Under Armour",
                "jbl" => "JBL",
                _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(knownBrand)
            };
            break;
        }

        string? category = null;
        if (ContainsAny(text, "zapatilla", "running", "calzado")) category = "Calzado";
        else if (ContainsAny(text, "futbol", "fútbol", "football", "footbol", "fulbo", "soccer", "botines", "pelota")) category = "Futbol";
        else if (ContainsAny(text, "remera", "short", "calza", "camiseta", "indumentaria", "ropa")) category = "Ropa Deportiva";
        else if (ContainsAny(text, "mochila", "bolso", "gorra", "media", "accesorio")) category = "Accesorios";
        else if (ContainsAny(text, "mancuerna", "banda", "colchoneta", "gym")) category = "Equipamiento Gym";
        else if (ContainsAny(text, "boxeo", "guantes", "bolsa")) category = "Boxeo";
        else if (ContainsAny(text, "tenis", "raqueta")) category = "Tenis";

        decimal? minPrice = null;
        decimal? maxPrice = null;
        var between = Regex.Match(text, @"(entre|de)\s+(\d[\d\.,]*)\s+(y|a)\s+(\d[\d\.,]*)", RegexOptions.CultureInvariant);
        if (between.Success)
        {
            minPrice = TryParsePrice(between.Groups[2].Value);
            maxPrice = TryParsePrice(between.Groups[4].Value);
            if (minPrice.HasValue && maxPrice.HasValue && minPrice > maxPrice)
            {
                (minPrice, maxPrice) = (maxPrice, minPrice);
            }
        }
        else
        {
            var upTo = Regex.Match(text, @"(hasta|menos de|maximo|max)\s+(\d[\d\.,]*)", RegexOptions.CultureInvariant);
            if (upTo.Success)
                maxPrice = TryParsePrice(upTo.Groups[2].Value);

            var from = Regex.Match(text, @"(desde|mas de|minimo|min)\s+(\d[\d\.,]*)", RegexOptions.CultureInvariant);
            if (from.Success)
                minPrice = TryParsePrice(from.Groups[2].Value);
        }

        var inStockOnly = ContainsAny(text, "en stock", "disponible", "disponibles", "hay");

        return new SearchFilters(brand, category, minPrice, maxPrice, inStockOnly);
    }

    private static decimal? TryParsePrice(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(digits))
            return null;

        return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool ContainsAny(string text, params string[] words)
    {
        return words.Any(word => text.Contains(word, StringComparison.Ordinal));
    }

    private static string BuildVectorLiteral(float[] embedding)
    {
        if (embedding.Length == 0)
            return "[]";

        var builder = new StringBuilder(embedding.Length * 8);
        builder.Append('[');
        for (var i = 0; i < embedding.Length; i++)
        {
            if (i > 0)
                builder.Append(',');

            var value = embedding[i];
            if (float.IsNaN(value) || float.IsInfinity(value))
                value = 0f;

            builder.Append(value.ToString("G9", CultureInfo.InvariantCulture));
        }

        builder.Append(']');
        return builder.ToString();
    }

    private sealed record SearchFilters(
        string? Brand,
        string? Category,
        decimal? MinPrice,
        decimal? MaxPrice,
        bool InStockOnly)
    {
        public static SearchFilters Empty { get; } = new(null, null, null, null, false);
    }

    private async Task<SqlConnection> OpenTenantConnectionAsync(Guid tenantId, CancellationToken ct)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var setTenantCommand = new SqlCommand(
            "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId, @read_only=0;",
            conn);
        setTenantCommand.Parameters.AddWithValue("@tenantId", tenantId);
        await setTenantCommand.ExecuteNonQueryAsync(ct);

        return conn;
    }
}
