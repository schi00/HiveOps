using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Infrastructure.Support;

/// <summary>
/// Manages the per-tenant <see cref="TenantContextPack"/>: a durable knowledge pack
/// (business glossary, database profile, repo profiles, resolution memory) plus
/// document ingestion/distillation that enriches it.
///
/// Design notes:
/// - Scope-agnostic: holds only the scoped <see cref="AppDbContext"/> and the
///   singleton-safe <see cref="IMemoryCache"/>; no other long-lived state. Register
///   it scoped, or as a singleton resolved through <c>IServiceScopeFactory</c>.
/// - Tenant isolation: methods take an explicit tenantId and always predicate on
///   <c>TenantId == tenantId</c> with <c>IgnoreQueryFilters()</c>
///   because the service may run in background flows where TenantContext is unresolved.
/// - Every mutation bumps <see cref="TenantContextPack.Version"/>, refreshes
///   <see cref="TenantContextPack.UpdatedAtUtc"/>, rebuilds the prompt digest and
///   invalidates the digest cache entry — all inside the same SaveChanges so digest
///   and data stay atomic.
/// - All LLM calls go through an injectable chat-completion seam (same pattern as
///   RootCauseHypothesisService) so tests never touch Semantic Kernel.
/// </summary>
public sealed class TenantContextPackService : ITenantContextPackService
{
    private static readonly TimeSpan DigestCacheTtl = TimeSpan.FromMinutes(5);

    private const int DigestMaxChars = 6000;
    private const int DigestMaxGlossaryTerms = 40;
    private const int DigestMaxMemoryEntries = 15;

    private const int MaxGlossaryTerms = 300;
    private const int MaxMemoryEntries = 100;
    private const int MaxDocumentBytes = 512 * 1024;
    private const int MaxChunksPerDocument = 500;
    private const int ChunkTargetChars = 1500;
    private const int ChunkOverlapChars = 200;
    private const int DistillSampleChars = 12000;
    private const int MaxProfileTables = 20;
    private const int MaxSchemaCharsForPrompt = 10000;
    private const int MaxRepoCount = 10;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".sql", ".json", ".csv",
        ".log", ".ddl", ".yaml", ".yml", ".html"
    };

    private static readonly Regex HeadingPattern = new(
        @"^\s{0,3}#{1,6}\s+(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex HtmlTagPattern = new(
        @"<[^>]+>",
        RegexOptions.Compiled);

    private static readonly Regex HtmlWhitespacePattern = new(
        @"[ \t]+",
        RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ITargetDatabaseDiagnosticService _targetDb;
    private readonly ITenantConfigService _tenantConfigService;
    private readonly KernelFactory _kernelFactory;
    private readonly IOptions<HiveOpsDeploymentOptions> _deploymentOptions;
    private readonly ILogger<TenantContextPackService> _logger;
    private readonly Func<Guid, string, string, CancellationToken, Task<string>> _chatCompletion;
    private readonly ITenantImprovementService? _improvements;
    private readonly ILlmTaskQueue? _taskQueue;

    public TenantContextPackService(
        AppDbContext db,
        IMemoryCache cache,
        ITargetDatabaseDiagnosticService targetDb,
        ITenantConfigService tenantConfigService,
        KernelFactory kernelFactory,
        IOptions<HiveOpsDeploymentOptions> deploymentOptions,
        ILogger<TenantContextPackService> logger,
        ITenantImprovementService? improvements = null,
        ILlmTaskQueue? taskQueue = null)
        : this(db, cache, targetDb, tenantConfigService, kernelFactory,
            deploymentOptions, logger, chatCompletion: null, improvements, taskQueue)
    {
    }

    /// <summary>
    /// Test seam: <paramref name="chatCompletion"/> replaces the Semantic Kernel
    /// round-trip so digest/distill logic can be unit-tested deterministically.
    /// </summary>
    internal TenantContextPackService(
        AppDbContext db,
        IMemoryCache cache,
        ITargetDatabaseDiagnosticService targetDb,
        ITenantConfigService tenantConfigService,
        KernelFactory kernelFactory,
        IOptions<HiveOpsDeploymentOptions> deploymentOptions,
        ILogger<TenantContextPackService> logger,
        Func<Guid, string, string, CancellationToken, Task<string>>? chatCompletion,
        ITenantImprovementService? improvements = null,
        ILlmTaskQueue? taskQueue = null)
    {
        _db = db;
        _cache = cache;
        _targetDb = targetDb;
        _tenantConfigService = tenantConfigService;
        _kernelFactory = kernelFactory;
        _deploymentOptions = deploymentOptions;
        _logger = logger;
        _chatCompletion = chatCompletion ?? CompleteChatAsync;
        _improvements = improvements;
        _taskQueue = taskQueue;
    }

    /// <inheritdoc />
    public async Task<TenantContextPack> GetOrCreateAsync(Guid tenantId, CancellationToken ct = default)
    {
        var pack = await _db.TenantContextPacks
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);

        if (pack is not null)
            return pack;

        pack = new TenantContextPack
        {
            TenantId = tenantId,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        pack.PromptDigest = BuildDigest(pack);
        _db.TenantContextPacks.Add(pack);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Unique-violation race: another request created the pack concurrently.
            // Detach the failed insert and re-read the winner.
            _db.Entry(pack).State = EntityState.Detached;
            var existing = await _db.TenantContextPacks
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
            if (existing is not null)
                return existing;
            throw;
        }

        return pack;
    }

    /// <inheritdoc />
    public Task<TenantContextPack?> GetAsync(Guid tenantId, CancellationToken ct = default) =>
        _db.TenantContextPacks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);

    /// <inheritdoc />
    public async Task<string> GetPromptDigestAsync(Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            var cacheKey = DigestCacheKey(tenantId);
            if (_cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
                return cached;

            var pack = await _db.TenantContextPacks
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);

            var digest = pack?.PromptDigest ?? string.Empty;
            _cache.Set(cacheKey, digest, DigestCacheTtl);
            return digest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load prompt digest for tenant {TenantId}.", tenantId);
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public async Task<TenantContextPack> RefreshDatabaseProfileAsync(
        Guid tenantId, string? updatedBy, CancellationToken ct = default)
    {
        var pack = await GetOrCreateAsync(tenantId, ct);

        // Without a dedicated target database the resolver falls back to the
        // platform's own schema — the profile must say so instead of silently
        // presenting platform tables as the tenant's business data.
        var hasDedicatedDb = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.EncryptedConnectionString != null && t.EncryptedConnectionString != "")
            .FirstOrDefaultAsync(ct);

        var schemaDdl = string.Empty;
        IReadOnlyList<string> candidateTables = [];
        try
        {
            var schema = await _targetDb.GetRelevantSchemaAsync(tenantId, string.Empty, ct);
            schemaDdl = schema.SchemaDdl ?? string.Empty;
            candidateTables = schema.CandidateTables ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Schema introspection failed for tenant {TenantId}; building a degraded profile.", tenantId);
        }

        JsonObject? profile = null;
        var llmProfilingFailed = false;
        if (!string.IsNullOrWhiteSpace(schemaDdl))
        {
            var systemPrompt = $$"""
                You are the HiveOps database profiler. Given the DDL of a tenant's database, produce a compact operational profile.

                RULES:
                - criticalTables: at most {{MaxProfileTables}} tables, ordered by operational importance. Each entry: { "name": "schema.table", "purpose": "one-line business purpose" }.
                - conventions: short text describing naming/typing conventions you observe (ids, timestamps, soft deletes, enums-as-text, etc.).
                - knownAnomalies: array of short strings describing suspicious or unusual constructs you can infer; empty array if none.

                Respond ONLY with valid JSON (no markdown fences):
                { "criticalTables": [ { "name": "...", "purpose": "..." } ], "conventions": "...", "knownAnomalies": [] }
                """;

            try
            {
                var raw = await _chatCompletion(
                    tenantId,
                    systemPrompt,
                    $"SCHEMA DDL:\n{Truncate(schemaDdl, MaxSchemaCharsForPrompt)}",
                    ct);
                profile = TryParseJsonObject(raw);
                if (profile is null)
                    llmProfilingFailed = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                llmProfilingFailed = true;
                _logger.LogWarning(ex,
                    "LLM database profiling failed for tenant {TenantId}; using raw table list.", tenantId);

                if (_taskQueue is not null)
                    await _taskQueue.EnqueueAsync(
                        tenantId,
                        LlmPendingTask.TaskDbProfile,
                        "{}",
                        $"db-profile:{tenantId}",
                        ex.Message,
                        ct);
            }
        }

        profile ??= BuildFallbackDatabaseProfile(candidateTables, schemaDdl);
        // Lets the retry worker distinguish an LLM-enriched profile from the
        // degraded raw-table fallback — a fallback refresh must not complete
        // a queued DbProfile task.
        profile["profiledBy"] = llmProfilingFailed ? "fallback" : "llm";

        // Preserve free-form notes accumulated by document ingestion.
        var existingNotes = TryGetArray(pack.DatabaseProfileJson, "notes");
        if (existingNotes is { Count: > 0 })
            profile["notes"] = existingNotes.DeepClone();

        profile["usesPlatformDatabase"] = !hasDedicatedDb;
        profile["refreshedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
        pack.DatabaseProfileJson = profile.ToJsonString();

        await MutateAndSaveAsync(pack, updatedBy, ct);
        return pack;
    }

    /// <inheritdoc />
    public async Task<TenantContextPack> RebuildResolutionMemoryAsync(
        Guid tenantId, int takeLast = 50, string? updatedBy = null, CancellationToken ct = default)
    {
        var pack = await GetOrCreateAsync(tenantId, ct);
        var take = Math.Clamp(takeLast, 1, MaxMemoryEntries);

        var resolved = await _db.Incidents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId
                && (i.Status == IncidentStatus.Resolved
                    || i.Status == IncidentStatus.Closed
                    || i.ResolvedAt != null))
            .OrderByDescending(i => i.ResolvedAt ?? i.UpdatedAt)
            .Take(take)
            .ToListAsync(ct);

        var escalated = await _db.Incidents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId
                && i.AssignedTo == "engineer"
                && i.Status != IncidentStatus.Resolved
                && i.Status != IncidentStatus.Closed
                && i.ResolvedAt == null)
            .OrderByDescending(i => i.UpdatedAt)
            .Take(take)
            .ToListAsync(ct);

        var entries = new List<JsonObject>(resolved.Count + escalated.Count);
        foreach (var incident in resolved)
            entries.Add(BuildMemoryEntry(incident, succeeded: true, notes: null));
        foreach (var incident in escalated)
            entries.Add(BuildMemoryEntry(incident, succeeded: false, notes: null));

        // Newest first across both buckets.
        var memory = new JsonArray();
        foreach (var entry in entries
                     .OrderByDescending(e => TryGetDateTimeOffset(e, "atUtc") ?? DateTimeOffset.MinValue)
                     .Take(MaxMemoryEntries))
        {
            memory.Add(entry);
        }

        pack.ResolutionMemoryJson = memory.ToJsonString();

        await MutateAndSaveAsync(pack, updatedBy, ct);
        return pack;
    }

    /// <inheritdoc />
    public async Task AppendResolutionMemoryAsync(
        Guid tenantId, Incident incident, bool succeeded, string? notes, CancellationToken ct = default)
    {
        try
        {
            var pack = await GetOrCreateAsync(tenantId, ct);

            var memory = TryParseJsonArray(pack.ResolutionMemoryJson) ?? new JsonArray();
            // Newest-first ring buffer: prepend, then drop the oldest entries.
            memory.Insert(0, BuildMemoryEntry(incident, succeeded, notes));
            while (memory.Count > MaxMemoryEntries)
                memory.RemoveAt(memory.Count - 1);

            pack.ResolutionMemoryJson = memory.ToJsonString();

            await MutateAndSaveAsync(pack, updatedBy: null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fire-and-forget safe: never rethrow.
            _logger.LogWarning(ex,
                "Failed to append resolution memory for tenant {TenantId} (incident {IncidentId}).",
                tenantId, incident.Id);
        }

        // Auto-ticket: every failed/escalated incident files (or bumps) an improvement
        // suggestion through this single choke point. Log-only — never affects the
        // resolution hot path.
        if (!succeeded && _improvements is not null)
        {
            try
            {
                await _improvements.EnsureForFailedIncidentAsync(tenantId, incident, notes, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Improvement auto-ticket failed for tenant {TenantId} (incident {IncidentId}).",
                    tenantId, incident.Id);
            }
        }
    }

    /// <inheritdoc />
    public async Task<TenantDocument> IngestDocumentAsync(
        Guid tenantId, string fileName, string contentType, string textContent,
        string? uploadedBy, CancellationToken ct = default)
    {
        var pack = await GetOrCreateAsync(tenantId, ct);

        var extension = Path.GetExtension(fileName ?? string.Empty);
        var sizeBytes = Encoding.UTF8.GetByteCount(textContent ?? string.Empty);

        var doc = new TenantDocument
        {
            TenantId = tenantId,
            FileName = fileName ?? string.Empty,
            ContentType = contentType ?? string.Empty,
            SizeBytes = sizeBytes,
            UploadedBy = uploadedBy,
            Status = "Pending"
        };

        if (!SupportedExtensions.Contains(extension))
        {
            doc.Status = "Failed";
            doc.DistilledSummary = "Formato no soportado";
            doc.ProcessedAtUtc = DateTimeOffset.UtcNow;
            _db.TenantDocuments.Add(doc);
            await _db.SaveChangesAsync(ct);
            InvalidateDigest(tenantId);
            return doc;
        }

        if (sizeBytes > MaxDocumentBytes)
        {
            doc.Status = "Failed";
            doc.DistilledSummary = "Documento excede el tamaño máximo permitido (512 KB)";
            doc.ProcessedAtUtc = DateTimeOffset.UtcNow;
            _db.TenantDocuments.Add(doc);
            await _db.SaveChangesAsync(ct);
            InvalidateDigest(tenantId);
            return doc;
        }

        var text = extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            ? StripHtml(textContent ?? string.Empty)
            : (textContent ?? string.Empty);

        var chunks = ChunkText(text);
        doc.ChunkCount = chunks.Count;
        _db.TenantDocuments.Add(doc);

        for (var i = 0; i < chunks.Count; i++)
        {
            _db.TenantDocumentChunks.Add(new TenantDocumentChunk
            {
                TenantId = tenantId,
                DocumentId = doc.Id,
                ChunkIndex = i,
                SectionTitle = chunks[i].SectionTitle,
                Content = chunks[i].Content
            });
        }

        await DistillDocumentContentAsync(pack, doc, fileName ?? string.Empty, text, ct);
        await MutateAndSaveAsync(pack, uploadedBy, ct);
        return doc;
    }

    /// <inheritdoc />
    public async Task<TenantDocument?> RetryDistillDocumentAsync(
        Guid tenantId, Guid documentId, CancellationToken ct = default)
    {
        var doc = await _db.TenantDocuments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == documentId, ct);
        if (doc is null || string.Equals(doc.Status, "Distilled", StringComparison.OrdinalIgnoreCase))
            return doc;

        var chunks = await _db.TenantDocumentChunks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.DocumentId == documentId)
            .OrderBy(c => c.ChunkIndex)
            .ToListAsync(ct);
        if (chunks.Count == 0)
            return doc;

        var pack = await GetOrCreateAsync(tenantId, ct);
        var text = string.Join('\n', chunks.Select(c => c.Content));
        await DistillDocumentContentAsync(pack, doc, doc.FileName, text, ct);
        await MutateAndSaveAsync(pack, "retry-worker", ct);
        return doc;
    }

    /// <summary>
    /// LLM distillation shared by ingest and the retry worker. On failure the work
    /// is enqueued (<see cref="ILlmTaskQueue"/>) so nothing is silently dropped —
    /// the document keeps its chunks and retried distillation resumes on recovery.
    /// </summary>
    private async Task DistillDocumentContentAsync(
        TenantContextPack pack, TenantDocument doc, string fileName, string text, CancellationToken ct)
    {
        try
        {
            var systemPrompt = $$"""
                You are the HiveOps context distiller. Given a tenant-provided document (runbook, schema notes, conventions), extract durable knowledge.

                RULES:
                - glossary: object mapping business/domain terms to { "meaning": "...", "tables": ["..."], "columns": ["..."] }. Only include terms explicitly grounded in the document; empty object if none.
                - databaseNotes: array of short strings with database facts/conventions worth remembering; empty array if none.
                - repoHints: array of short strings with codebase/repo facts worth remembering; empty array if none.
                - summary: one line (max ~120 chars) describing what this document contributes.

                Respond ONLY with valid JSON (no markdown fences):
                { "glossary": { "term": { "meaning": "...", "tables": [], "columns": [] } }, "databaseNotes": [], "repoHints": [], "summary": "..." }
                """;

            var raw = await _chatCompletion(
                doc.TenantId,
                systemPrompt,
                $"DOCUMENT: {fileName}\n\n{Truncate(text, DistillSampleChars)}",
                ct);

            var distilled = TryParseJsonObject(raw);
            if (distilled is null)
                throw new JsonException("LLM returned an unparseable distillation payload.");

            MergeGlossary(pack, distilled);
            AppendProfileNotes(pack, distilled);
            AppendRepoNotes(pack, distilled);

            doc.Status = "Distilled";
            doc.DistilledSummary =
                distilled["summary"]?.GetValue<string>() is { Length: > 0 } s
                    ? Truncate(s, 200)
                    : "Doc procesado";
            doc.ProcessedAtUtc = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Document distillation failed for tenant {TenantId} doc {FileName}; chunks are kept for retry.",
                doc.TenantId, fileName);
            doc.Status = "Failed";
            doc.DistilledSummary = "Destilación fallida; chunks conservados";
            doc.ProcessedAtUtc = DateTimeOffset.UtcNow;

            if (_taskQueue is not null)
            {
                var queued = await _taskQueue.EnqueueAsync(
                    doc.TenantId,
                    LlmPendingTask.TaskDocDistill,
                    JsonSerializer.Serialize(new { documentId = doc.Id }),
                    $"doc-distill:{doc.Id}",
                    ex.Message,
                    ct);
                if (queued is not null)
                    doc.DistilledSummary = "Destilación diferida; reintento programado";
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantDocument>> ListDocumentsAsync(
        Guid tenantId, CancellationToken ct = default) =>
        await _db.TenantDocuments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId)
            .OrderByDescending(d => d.UploadedAtUtc)
            .ToListAsync(ct);

    /// <inheritdoc />
    public async Task<bool> DeleteDocumentAsync(
        Guid tenantId, Guid documentId, CancellationToken ct = default)
    {
        var doc = await _db.TenantDocuments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == documentId, ct);
        if (doc is null)
            return false;

        var chunks = await _db.TenantDocumentChunks
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.DocumentId == documentId)
            .ToListAsync(ct);

        _db.TenantDocumentChunks.RemoveRange(chunks);
        _db.TenantDocuments.Remove(doc);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<TenantContextPack?> UpdateSectionAsync(
        Guid tenantId, string sectionName, string sectionJson, string? updatedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sectionJson))
            return null;

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(sectionJson);
        }
        catch (JsonException)
        {
            return null;
        }

        string normalized;
        string? field;
        using (parsed)
        {
            normalized = parsed.RootElement.GetRawText();
            field = sectionName?.Trim().ToLowerInvariant() switch
            {
                "domainglossary" => nameof(TenantContextPack.DomainGlossaryJson),
                "databaseprofile" => nameof(TenantContextPack.DatabaseProfileJson),
                "repoprofiles" => nameof(TenantContextPack.RepoProfilesJson),
                "resolutionmemory" => nameof(TenantContextPack.ResolutionMemoryJson),
                _ => null
            };
        }

        if (field is null)
            return null;

        var pack = await GetOrCreateAsync(tenantId, ct);
        switch (field)
        {
            case nameof(TenantContextPack.DomainGlossaryJson):
                pack.DomainGlossaryJson = normalized;
                break;
            case nameof(TenantContextPack.DatabaseProfileJson):
                pack.DatabaseProfileJson = normalized;
                break;
            case nameof(TenantContextPack.RepoProfilesJson):
                pack.RepoProfilesJson = normalized;
                break;
            case nameof(TenantContextPack.ResolutionMemoryJson):
                pack.ResolutionMemoryJson = normalized;
                break;
        }

        await MutateAndSaveAsync(pack, updatedBy, ct);
        return pack;
    }

    /// <inheritdoc />
    public async Task<string> ExportMarkdownAsync(Guid tenantId, CancellationToken ct = default)
    {
        var pack = await GetAsync(tenantId, ct);
        var sb = new StringBuilder();
        sb.AppendLine("# Contexto del Tenant");
        sb.AppendLine();

        if (pack is null)
        {
            sb.AppendLine("_Sin contexto cargado para este tenant._");
            return sb.ToString();
        }

        // Glosario
        sb.AppendLine("## Glosario");
        var glossary = TryParseJsonObject(pack.DomainGlossaryJson);
        if (glossary is null || glossary.Count == 0)
        {
            sb.AppendLine("_Vacío_");
        }
        else
        {
            sb.AppendLine("| Término | Significado | Tablas | Columnas |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var kv in glossary)
            {
                var meaning = kv.Value?["meaning"]?.GetValue<string>() ?? string.Empty;
                var tables = RenderStringArray(kv.Value?["tables"]);
                var columns = RenderStringArray(kv.Value?["columns"]);
                sb.Append("| ").Append(EscapeMdCell(kv.Key))
                  .Append(" | ").Append(EscapeMdCell(meaning))
                  .Append(" | ").Append(EscapeMdCell(tables))
                  .Append(" | ").Append(EscapeMdCell(columns))
                  .AppendLine(" |");
            }
        }
        sb.AppendLine();

        // Perfil de base de datos
        sb.AppendLine("## Perfil de base de datos");
        var profile = TryParseJsonObject(pack.DatabaseProfileJson);
        if (profile is null || profile.Count == 0)
        {
            sb.AppendLine("_Vacío_");
        }
        else
        {
            var refreshed = profile["refreshedAtUtc"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(refreshed))
                sb.AppendLine($"_Actualizado: {refreshed}_").AppendLine();

            if (profile["criticalTables"] is JsonArray tables && tables.Count > 0)
            {
                sb.AppendLine("| Tabla | Propósito |");
                sb.AppendLine("| --- | --- |");
                foreach (var t in tables)
                {
                    sb.Append("| ").Append(EscapeMdCell(t?["name"]?.GetValue<string>() ?? string.Empty))
                      .Append(" | ").Append(EscapeMdCell(t?["purpose"]?.GetValue<string>() ?? string.Empty))
                      .AppendLine(" |");
                }
                sb.AppendLine();
            }

            var conventions = profile["conventions"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(conventions))
                sb.AppendLine("**Convenciones:** ").AppendLine(conventions).AppendLine();

            if (profile["knownAnomalies"] is JsonArray anomalies && anomalies.Count > 0)
            {
                sb.AppendLine("**Anomalías conocidas:**");
                foreach (var a in anomalies)
                    sb.AppendLine("- ").AppendLine(a?.GetValue<string>() ?? string.Empty);
                sb.AppendLine();
            }

            if (profile["notes"] is JsonArray notes && notes.Count > 0)
            {
                sb.AppendLine("**Notas:**");
                foreach (var n in notes)
                    sb.AppendLine("- ").AppendLine(n?.GetValue<string>() ?? string.Empty);
            }
        }
        sb.AppendLine();

        // Repositorios
        sb.AppendLine("## Repositorios");
        var repos = TryParseJsonObject(pack.RepoProfilesJson);
        var repoList = repos?["repos"] as JsonArray;
        if (repoList is null || repoList.Count == 0)
        {
            sb.AppendLine("_Vacío_");
        }
        else
        {
            foreach (var r in repoList)
            {
                sb.Append("- **").Append(r?["name"]?.GetValue<string>() ?? "?").Append("**");
                var modules = RenderStringArray(r?["modules"]);
                if (!string.IsNullOrWhiteSpace(modules))
                    sb.Append(" — módulos: ").Append(modules);
                var conventions = r?["conventions"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(conventions))
                    sb.Append(" — ").Append(conventions);
                sb.AppendLine();
            }
        }
        if (repos?["notes"] is JsonArray repoNotes && repoNotes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Notas de repositorio:**");
            foreach (var n in repoNotes)
                sb.AppendLine("- ").AppendLine(n?.GetValue<string>() ?? string.Empty);
        }
        sb.AppendLine();

        // Memoria de resoluciones
        sb.AppendLine("## Memoria de resoluciones");
        var memory = TryParseJsonArray(pack.ResolutionMemoryJson);
        if (memory is null || memory.Count == 0)
        {
            sb.AppendLine("_Vacío_");
        }
        else
        {
            sb.AppendLine("| Cuándo | Síntoma | Resultado | Fix |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var e in memory)
            {
                var at = e?["atUtc"]?.GetValue<string>() ?? string.Empty;
                var symptom = e?["symptom"]?.GetValue<string>() ?? string.Empty;
                var succeeded = e?["succeeded"]?.GetValue<bool>() ?? false;
                var fix = succeeded
                    ? e?["fixThatWorked"]?.GetValue<string>()
                    : e?["fixThatFailed"]?.GetValue<string>();
                sb.Append("| ").Append(EscapeMdCell(at))
                  .Append(" | ").Append(EscapeMdCell(symptom))
                  .Append(" | ").Append(succeeded ? "OK" : "Falló")
                  .Append(" | ").Append(EscapeMdCell(fix ?? string.Empty))
                  .AppendLine(" |");
            }
        }
        sb.AppendLine();

        // Digest inyectado
        sb.AppendLine("## Digest inyectado al agente");
        sb.AppendLine("```text");
        sb.AppendLine(pack.PromptDigest ?? string.Empty);
        sb.AppendLine("```");

        return sb.ToString();
    }

    // ─── Mutation plumbing ──────────────────────────────────────────────────

    /// <summary>
    /// Applies the standard post-mutation bookkeeping: timestamps, version bump,
    /// digest rebuild (before SaveChanges so both are atomic), persistence and
    /// cache invalidation.
    /// </summary>
    private async Task MutateAndSaveAsync(TenantContextPack pack, string? updatedBy, CancellationToken ct)
    {
        pack.UpdatedAtUtc = DateTimeOffset.UtcNow;
        pack.UpdatedBy = updatedBy ?? pack.UpdatedBy;
        pack.Version++;
        pack.PromptDigest = BuildDigest(pack);

        await _db.SaveChangesAsync(ct);
        InvalidateDigest(pack.TenantId);
    }

    private void InvalidateDigest(Guid tenantId) => _cache.Remove(DigestCacheKey(tenantId));

    private static string DigestCacheKey(Guid tenantId) => $"ctxpack:{tenantId}:digest";

    // ─── Digest builder ─────────────────────────────────────────────────────

    /// <summary>
    /// Renders the condensed prompt digest (&lt;=6000 chars) injected into
    /// investigation prompts. Optimized for information density.
    /// </summary>
    private static string BuildDigest(TenantContextPack pack)
    {
        var sb = new StringBuilder(DigestMaxChars);
        sb.Append("CONTEXTO DEL CLIENTE");

        // Glosario (top ~40 terms)
        var glossary = TryParseJsonObject(pack.DomainGlossaryJson);
        if (glossary is { Count: > 0 })
        {
            sb.Append("\n\n### Glosario\n");
            var count = 0;
            foreach (var kv in glossary)
            {
                if (count >= DigestMaxGlossaryTerms)
                    break;
                var meaning = kv.Value?["meaning"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(meaning))
                    continue;
                sb.Append("- ").Append(kv.Key).Append(" → ").Append(meaning);
                var tables = RenderStringArray(kv.Value?["tables"]);
                if (!string.IsNullOrWhiteSpace(tables))
                    sb.Append(" (tablas: ").Append(tables).Append(')');
                sb.Append('\n');
                count++;
            }
        }

        // Base de datos
        var profile = TryParseJsonObject(pack.DatabaseProfileJson);
        if (profile is { Count: > 0 })
        {
            var tableNames = (profile["criticalTables"] as JsonArray)?
                .Select(t => t?["name"]?.GetValue<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Take(MaxProfileTables)
                .ToList() ?? [];
            var conventions = profile["conventions"]?.GetValue<string>();
            var anomalies = (profile["knownAnomalies"] as JsonArray)?
                .Select(a => a?.GetValue<string>())
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Take(10)
                .ToList() ?? [];

            if (tableNames.Count > 0 || !string.IsNullOrWhiteSpace(conventions) || anomalies.Count > 0)
            {
                sb.Append("\n### Base de datos\n");
                if (tableNames.Count > 0)
                    sb.Append("Tablas: ").Append(string.Join(", ", tableNames)).Append('\n');
                if (!string.IsNullOrWhiteSpace(conventions))
                    sb.Append("Convenciones: ").Append(conventions).Append('\n');
                foreach (var a in anomalies)
                    sb.Append("Anomalía: ").Append(a).Append('\n');
            }
        }

        // Repos
        var repos = TryParseJsonObject(pack.RepoProfilesJson);
        var repoList = repos?["repos"] as JsonArray;
        if (repoList is { Count: > 0 })
        {
            sb.Append("\n### Repos\n");
            foreach (var r in repoList.Take(MaxRepoCount))
            {
                var name = r?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                sb.Append("- ").Append(name);
                var modules = RenderStringArray(r?["modules"]);
                if (!string.IsNullOrWhiteSpace(modules))
                    sb.Append(" (módulos: ").Append(modules).Append(')');
                sb.Append('\n');
            }
        }

        // Memoria de fixes (last ~15)
        var memory = TryParseJsonArray(pack.ResolutionMemoryJson);
        if (memory is { Count: > 0 })
        {
            sb.Append("\n### Memoria de fixes\n");
            foreach (var e in memory.Take(DigestMaxMemoryEntries))
            {
                var symptom = e?["symptom"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(symptom))
                    continue;
                var succeeded = e?["succeeded"]?.GetValue<bool>() ?? false;
                var fix = succeeded
                    ? e?["fixThatWorked"]?.GetValue<string>()
                    : e?["fixThatFailed"]?.GetValue<string>();
                if (!succeeded)
                    sb.Append("✗ ");
                sb.Append("- ").Append(symptom);
                if (!string.IsNullOrWhiteSpace(fix))
                    sb.Append(" → ").Append(fix);
                sb.Append('\n');
            }
        }

        var digest = sb.ToString().TrimEnd();
        return digest.Length <= DigestMaxChars
            ? digest
            : digest[..(DigestMaxChars - 1)] + "…";
    }

    // ─── Resolution memory mapping ──────────────────────────────────────────

    private static JsonObject BuildMemoryEntry(Incident incident, bool succeeded, string? notes)
    {
        var entry = new JsonObject
        {
            ["symptom"] = incident.Title ?? string.Empty,
            ["entity"] = ResolveEntity(incident),
            ["rootCause"] = ExtractRootCause(incident.RootCauseJson),
            ["succeeded"] = succeeded,
            ["atUtc"] = (incident.ResolvedAt ?? DateTimeOffset.UtcNow).ToString("O")
        };

        var fix = notes ?? incident.ResolutionNotes ?? incident.TechnicalDiagnosticNote;
        if (succeeded)
        {
            entry["fixThatWorked"] = fix;
            entry["fixThatFailed"] = null;
        }
        else
        {
            entry["fixThatWorked"] = null;
            entry["fixThatFailed"] = fix ?? ExtractRootCause(incident.RootCauseJson);
        }

        return entry;
    }

    private static string? ResolveEntity(Incident incident)
    {
        // Prefer a concrete entity extracted during triage; fall back to Category.
        if (!string.IsNullOrWhiteSpace(incident.ExtractedEntitiesJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(incident.ExtractedEntitiesJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "entity", "table", "service", "component" })
                    {
                        if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                        {
                            var value = el.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                                return value;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // fall through to Category
            }
        }

        return incident.Category.ToString();
    }

    private static string? ExtractRootCause(string? rootCauseJson)
    {
        if (string.IsNullOrWhiteSpace(rootCauseJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(rootCauseJson);
            var root = doc.RootElement;
            foreach (var key in new[] { "summaryHypothesis", "SummaryHypothesis", "rootCause", "summary" })
            {
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty(key, out var el)
                    && el.ValueKind == JsonValueKind.String)
                {
                    return el.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // fall through
        }
        return null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonObject obj, string key)
    {
        var raw = obj[key]?.GetValue<string>();
        return DateTimeOffset.TryParse(raw, out var dto) ? dto : null;
    }

    // ─── Document processing ────────────────────────────────────────────────

    private static string StripHtml(string html)
    {
        var noTags = HtmlTagPattern.Replace(html, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        var compact = HtmlWhitespacePattern.Replace(decoded, " ");
        return Regex.Replace(compact, @"\n\s*\n\s*\n+", "\n\n").Trim();
    }

    private sealed record TextChunk(string Content, string? SectionTitle);

    /// <summary>
    /// Splits text into ~1500-char chunks with ~200-char overlap, preferring
    /// paragraph/line boundaries. Each chunk carries the most recent markdown
    /// heading seen before its start as SectionTitle. Capped at 500 chunks.
    /// </summary>
    private static IReadOnlyList<TextChunk> ChunkText(string text)
    {
        var chunks = new List<TextChunk>();
        if (string.IsNullOrWhiteSpace(text))
            return chunks;

        var normalized = text.Replace("\r\n", "\n");

        // Precompute heading offsets so each chunk can find its section cheaply.
        var headings = new List<(int Offset, string Title)>();
        foreach (Match m in HeadingPattern.Matches(normalized))
            headings.Add((m.Index, m.Groups["title"].Value.Trim()));

        var pos = 0;
        while (pos < normalized.Length && chunks.Count < MaxChunksPerDocument)
        {
            var hardEnd = Math.Min(pos + ChunkTargetChars, normalized.Length);
            var end = hardEnd;
            if (end < normalized.Length)
            {
                // Prefer a paragraph break, then any line break, in the last 40% of the window.
                var windowStart = pos + (int)(ChunkTargetChars * 0.6);
                var paraBreak = normalized.LastIndexOf("\n\n", end - 1, StringComparison.Ordinal);
                var lineBreak = normalized.LastIndexOf('\n', end - 1);
                if (paraBreak >= windowStart)
                    end = paraBreak + 2;
                else if (lineBreak >= windowStart)
                    end = lineBreak + 1;
            }

            var content = normalized[pos..end].Trim();
            if (content.Length > 0)
            {
                var section = LastHeadingBefore(headings, pos);
                chunks.Add(new TextChunk(content, section));
            }

            if (end >= normalized.Length)
                break;

            // Overlap: step back ~200 chars to a line boundary, always making progress.
            var overlapStart = Math.Max(pos + 1, end - ChunkOverlapChars);
            var nextBoundary = normalized.IndexOf('\n', overlapStart);
            pos = nextBoundary > 0 && nextBoundary < end ? nextBoundary + 1 : overlapStart;
            if (pos >= normalized.Length)
                break;
        }

        return chunks;
    }

    private static string? LastHeadingBefore(List<(int Offset, string Title)> headings, int pos)
    {
        string? title = null;
        foreach (var (offset, t) in headings)
        {
            if (offset <= pos)
                title = t;
            else
                break;
        }
        return title;
    }

    // ─── Pack JSON mutation helpers ─────────────────────────────────────────

    /// <summary>Merges distilled glossary terms; new document terms override existing keys. Capped at 300.</summary>
    private static void MergeGlossary(TenantContextPack pack, JsonObject distilled)
    {
        if (distilled["glossary"] is not JsonObject newTerms || newTerms.Count == 0)
            return;

        var glossary = TryParseJsonObject(pack.DomainGlossaryJson) ?? new JsonObject();
        foreach (var kv in newTerms)
        {
            if (kv.Value is null)
                continue;
            // New document wins: refresh/override the same key.
            glossary[kv.Key] = kv.Value.DeepClone();
        }

        while (glossary.Count > MaxGlossaryTerms)
        {
            // JsonObject preserves insertion order: drop the oldest terms first.
            var oldest = glossary.First();
            glossary.Remove(oldest.Key);
        }

        pack.DomainGlossaryJson = glossary.ToJsonString();
    }

    /// <summary>Appends distilled databaseNotes to DatabaseProfile.notes.</summary>
    private static void AppendProfileNotes(TenantContextPack pack, JsonObject distilled)
    {
        if (distilled["databaseNotes"] is not JsonArray notes || notes.Count == 0)
            return;

        var profile = TryParseJsonObject(pack.DatabaseProfileJson) ?? new JsonObject();
        var existing = profile["notes"] as JsonArray ?? new JsonArray();
        foreach (var n in notes)
            existing.Add(n?.DeepClone());
        profile["notes"] = existing;
        pack.DatabaseProfileJson = profile.ToJsonString();
    }

    /// <summary>Appends distilled repoHints to RepoProfiles.notes.</summary>
    private static void AppendRepoNotes(TenantContextPack pack, JsonObject distilled)
    {
        if (distilled["repoHints"] is not JsonArray hints || hints.Count == 0)
            return;

        var repos = TryParseJsonObject(pack.RepoProfilesJson) ?? new JsonObject();
        var existing = repos["notes"] as JsonArray ?? new JsonArray();
        foreach (var h in hints)
            existing.Add(h?.DeepClone());
        repos["notes"] = existing;
        pack.RepoProfilesJson = repos.ToJsonString();
    }

    private static JsonObject BuildFallbackDatabaseProfile(
        IReadOnlyList<string> candidateTables, string schemaDdl)
    {
        var tables = new JsonArray();
        var names = candidateTables.Count > 0
            ? candidateTables
            : ExtractTableNamesFromDdl(schemaDdl);

        foreach (var name in names.Take(MaxProfileTables))
        {
            tables.Add(new JsonObject
            {
                ["name"] = name,
                ["purpose"] = string.Empty
            });
        }

        return new JsonObject
        {
            ["criticalTables"] = tables,
            ["conventions"] = string.Empty,
            ["knownAnomalies"] = new JsonArray()
        };
    }

    private static readonly Regex DdlTablePattern = new(
        @"TABLE\s+(?<name>[\w\.]+)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static IReadOnlyList<string> ExtractTableNamesFromDdl(string ddl)
    {
        if (string.IsNullOrWhiteSpace(ddl))
            return [];
        return DdlTablePattern.Matches(ddl)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ─── Tolerant JSON helpers ──────────────────────────────────────────────

    private static JsonObject? TryParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var node = JsonNode.Parse(StripMarkdownJsonFence(json.Trim()));
            return node as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonArray? TryParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var node = JsonNode.Parse(json);
            return node as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonArray? TryGetArray(string? json, string property)
    {
        var obj = TryParseJsonObject(json);
        return obj?[property] as JsonArray;
    }

    private static string RenderStringArray(JsonNode? node)
    {
        if (node is not JsonArray arr)
            return string.Empty;
        return string.Join(", ", arr
            .Select(e => e?.GetValue<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string StripMarkdownJsonFence(string raw)
    {
        if (!raw.StartsWith("```", StringComparison.Ordinal))
            return raw;

        var firstNl = raw.IndexOf('\n');
        if (firstNl < 0)
            return raw;

        var body = raw[(firstNl + 1)..];
        if (body.EndsWith("```", StringComparison.Ordinal))
            body = body[..^3].Trim();

        return body;
    }

    private static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    private static string EscapeMdCell(string value) =>
        (value ?? string.Empty).Replace("|", "\\|").Replace('\n', ' ').Replace('\r', ' ');

    // ─── LLM plumbing ───────────────────────────────────────────────────────

    private async Task<string> CompleteChatAsync(
        Guid tenantId, string systemPrompt, string userMessage, CancellationToken ct)
    {
        var kernel = _kernelFactory.CreateForTenant(tenantId);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userMessage);

        var exec = await TenantLlmExecutionHelper.GetTriageChatExecutionSettingsAsync(
            tenantId, _tenantConfigService, _deploymentOptions, ct);
        var response = await chat.GetChatMessageContentAsync(history, exec, cancellationToken: ct);
        return (response.Content ?? string.Empty).Trim();
    }
}
