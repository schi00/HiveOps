using HiveOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;

namespace HiveOps.Sandpit.Ui.Controllers;

[ApiController]
[Route("api/sandpit/db-faults")]
public class DatabaseFaultController(
    AppDbContext db,
    ILogger<DatabaseFaultController> logger,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration) : ControllerBase
{
    private static readonly Guid SandpitTenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private async Task CreateIncidentInHiveOpsAsync(string faultType, string description)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            var baseUrl = configuration["HiveOpsApi:BaseUrl"] ?? "https://localhost:5001";
            var apiKey = configuration["HiveOpsApi:ApiKey"];

            var payload = new
            {
                title = $"[SANDPIT-FAULT] {faultType}",
                description = description,
                severity = "Medium",
                category = "Code"
            };

            client.DefaultRequestHeaders.Add("X-Dev-Key", apiKey);
            var response = await client.PostAsJsonAsync($"{baseUrl}/api/support/incidents/from-sandpit", payload);
            response.EnsureSuccessStatusCode();

            logger.LogInformation("[SANDPIT] Incident created in HiveOps.Api for fault: {FaultType}", faultType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SANDPIT] Failed to create incident in HiveOps.Api for fault: {FaultType}", faultType);
        }
    }

    private static readonly string EnsureIncidentsSchema = @"
        IF OBJECT_ID('Incidents', 'U') IS NULL
            CREATE TABLE Incidents (
                Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
                Title NVARCHAR(500) NULL,
                Description NVARCHAR(MAX) NULL,
                Severity INT NOT NULL,
                TenantId UNIQUEIDENTIFIER NOT NULL,
                CreatedAt DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
            );";

    private static readonly string EnsureDiagnosticLogsSchema = @"
        IF OBJECT_ID('DiagnosticLogs', 'U') IS NULL
            CREATE TABLE DiagnosticLogs (
                Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
                TenantId UNIQUEIDENTIFIER NOT NULL,
                RawPayload NVARCHAR(MAX) NULL,
                CreatedAt DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
            );";

    private static readonly string EnsureConversationMessagesSchema = @"
        IF OBJECT_ID('ConversationMessages', 'U') IS NULL
        BEGIN
            CREATE TABLE ConversationMessages (
                Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
                ConversationId UNIQUEIDENTIFIER NOT NULL,
                Body NVARCHAR(MAX) NULL,
                TenantId UNIQUEIDENTIFIER NOT NULL
            );
            ALTER TABLE ConversationMessages WITH NOCHECK
                ADD CONSTRAINT FK_ConversationMessages_ConversationId
                FOREIGN KEY (ConversationId) REFERENCES Incidents(Id);
        END";

    [HttpPost("simulate-timeout")]
    public async Task<IActionResult> SimulateTimeout()
    {
        logger.LogWarning("[SANDBOX-FAULT] timeout | tenant={TenantId}", SandpitTenant);
        try
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "WAITFOR DELAY '00:00:05'";
            cmd.CommandTimeout = 1;
            await cmd.ExecuteNonQueryAsync();

            var description = "Se simulo un timeout de base de datos en el entorno Sandpit. " +
                "El comando SQL WAITFOR DELAY '00:00:05' se ejecuto con CommandTimeout=1 segundo, " +
                "lo que causo que la operacion excediera el tiempo limite permitido. " +
                "Este tipo de fallo indica problemas de rendimiento en consultas pesadas o bloqueos prolongados.";

            await CreateIncidentInHiveOpsAsync("Database Timeout", description);

            return Ok(new { fault = "timeout", message = "Query should have timed out — check if timeout threshold works" });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == -2)
        {
            logger.LogError(ex, "[SANDBOX-FAULT] timeout triggered genuine SqlException");

            var description = "Timeout de base de datos ocurrido en Sandpit. Error SQL: " + ex.Message + ". " +
                "El comando SQL excedio el tiempo limite de 1 segundo. " +
                "Este fallo puede afectar la experiencia del usuario y requiere optimizacion de consultas.";

            await CreateIncidentInHiveOpsAsync("Database Timeout Exception", description);

            return StatusCode(500, new
            {
                fault = "timeout",
                error = ex.Message,
                sqlError = ex.Number,
                message = "Genuine SQL timeout occurred (CommandTimeout=1s, query ran 5s)"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SANDBOX-FAULT] timeout simulation error");
            return StatusCode(500, new { fault = "timeout", error = ex.Message });
        }
    }

    [HttpPost("insert-corrupt-record")]
    public async Task<IActionResult> InsertCorruptRecord()
    {
        logger.LogWarning("[SANDBOX-FAULT] corrupt-record | tenant={TenantId}", SandpitTenant);
        try
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();

            using var schemaCmd = connection.CreateCommand();
            schemaCmd.CommandText = EnsureIncidentsSchema;
            await schemaCmd.ExecuteNonQueryAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Incidents (Id, Title, Description, Severity, TenantId, CreatedAt)
                VALUES
                    (NEWID(), NULL, NULL, 999, @tenant, SYSDATETIMEOFFSET()),
                    (NEWID(), '', '', -1, @tenant, SYSDATETIMEOFFSET());";
            var p = cmd.CreateParameter();
            p.ParameterName = "@tenant";
            p.Value = SandpitTenant;
            cmd.Parameters.Add(p);
            await cmd.ExecuteNonQueryAsync();

            var description = "Se insertaron registros corruptos en la tabla Incidents de Sandpit. " +
                "Los registros tienen valores invalidos: Title/Description NULL, Severity=999 y Severity=-1. " +
                "Este tipo de fallo indica falta de validacion de datos en la capa de persistencia.";

            await CreateIncidentInHiveOpsAsync("Corrupt Database Records", description);

            return Ok(new
            {
                fault = "corrupt-record",
                message = "Inserted records with NULL Title/Description, invalid Severity (999, -1), empty strings"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SANDBOX-FAULT] corrupt-record insertion failed");
            return StatusCode(500, new { fault = "corrupt-record", error = ex.Message });
        }
    }

    [HttpPost("simulate-blocking-lock")]
    public async Task<IActionResult> SimulateBlockingLock()
    {
        logger.LogWarning("[SANDBOX-FAULT] blocking-lock | tenant={TenantId}", SandpitTenant);
        try
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();

            using var schemaCmd = connection.CreateCommand();
            schemaCmd.CommandText = EnsureIncidentsSchema;
            await schemaCmd.ExecuteNonQueryAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                BEGIN TRANSACTION;
                SELECT TOP 1 * FROM Incidents WITH (HOLDLOCK, UPDLOCK);
                WAITFOR DELAY '00:00:02';
                ROLLBACK;";
            await cmd.ExecuteNonQueryAsync();

            var description = "Se simulo un bloqueo exclusivo (blocking lock) en la tabla Incidents de Sandpit. " +
                "La transaccion mantuvo un lock HOLDLOCK/UPDLOCK durante 2 segundos, " +
                "lo que bloquearia cualquier solicitud concurrente a la misma tabla. " +
                "Este tipo de fallo indica problemas de concurrencia que pueden causar deadlocks en produccion.";

            await CreateIncidentInHiveOpsAsync("Database Blocking Lock", description);

            return Ok(new { fault = "blocking-lock", message = "Held exclusive lock for 2s on Incidents — concurrent requests would block" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SANDBOX-FAULT] blocking-lock simulation error");
            return StatusCode(500, new { fault = "blocking-lock", error = ex.Message });
        }
    }

    [HttpPost("drop-foreign-key")]
    public async Task<IActionResult> DropForeignKey()
    {
        logger.LogWarning("[SANDBOX-FAULT] broken-integrity | tenant={TenantId}", SandpitTenant);
        try
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();

            using var schemaCmd1 = connection.CreateCommand();
            schemaCmd1.CommandText = EnsureIncidentsSchema;
            await schemaCmd1.ExecuteNonQueryAsync();

            using var schemaCmd2 = connection.CreateCommand();
            schemaCmd2.CommandText = EnsureConversationMessagesSchema;
            await schemaCmd2.ExecuteNonQueryAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                ALTER TABLE ConversationMessages NOCHECK CONSTRAINT ALL;

                INSERT INTO ConversationMessages (Id, ConversationId, Body, TenantId)
                VALUES (NEWID(), '00000000-0000-0000-0000-000000000000', 'Orphan message', @tenant);";
            var p = cmd.CreateParameter();
            p.ParameterName = "@tenant";
            p.Value = SandpitTenant;
            cmd.Parameters.Add(p);
            await cmd.ExecuteNonQueryAsync();

            var description = "Se simulo una violacion de integridad referencial en Sandpit. " +
                "Se deshabilitaron los checks de foreign keys (NOCHECK CONSTRAINT ALL) " +
                "y se inserto un registro huerfano con un ConversationId inexistente. " +
                "Este tipo de fallo indica problemas de integridad de datos que pueden causar inconsistencias en el sistema.";

            await CreateIncidentInHiveOpsAsync("Broken Referential Integrity", description);

            return Ok(new
            {
                fault = "broken-integrity",
                message = "Disabled FK checks and inserted orphan row with non-existent ConversationId"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SANDBOX-FAULT] broken-integrity simulation failed");
            return StatusCode(500, new { fault = "broken-integrity", error = ex.Message });
        }
    }

    [HttpPost("seed-invalid-json")]
    public async Task<IActionResult> SeedInvalidJson()
    {
        logger.LogWarning("[SANDBOX-FAULT] invalid-json | tenant={TenantId}", SandpitTenant);
        try
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();

            using var schemaCmd = connection.CreateCommand();
            schemaCmd.CommandText = EnsureDiagnosticLogsSchema;
            await schemaCmd.ExecuteNonQueryAsync();

            var invalidPayload = "{ \"status\": \"ok\", \"data\": [1, 2, 3,,,] }";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO DiagnosticLogs (Id, TenantId, RawPayload, CreatedAt)
                VALUES (NEWID(), @tenant, @payload, SYSDATETIMEOFFSET());";

            var p1 = cmd.CreateParameter();
            p1.ParameterName = "@tenant";
            p1.Value = SandpitTenant;
            cmd.Parameters.Add(p1);

            var p2 = cmd.CreateParameter();
            p2.ParameterName = "@payload";
            p2.Value = invalidPayload;
            cmd.Parameters.Add(p2);

            await cmd.ExecuteNonQueryAsync();

            bool failsParse = false;
            try { System.Text.Json.JsonDocument.Parse(invalidPayload); } catch { failsParse = true; }

            var description = "Se inserto JSON malformado en la tabla DiagnosticLogs de Sandpit. " +
                "El payload tiene comas trailing invalidas: { \"status\": \"ok\", \"data\": [1, 2, 3,,,] }. " +
                "Este tipo de fallo indica falta de validacion de JSON en la capa de entrada de datos, " +
                "lo que puede causar errores de parsing en el sistema.";

            await CreateIncidentInHiveOpsAsync("Invalid JSON Payload", description);

            return Ok(new
            {
                fault = "invalid-json",
                message = "Inserted malformed JSON with trailing commas; parse will fail",
                payloadPreview = invalidPayload,
                failsParse
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SANDBOX-FAULT] invalid-json seeding failed");
            return StatusCode(500, new { fault = "invalid-json", error = ex.Message });
        }
    }
}
