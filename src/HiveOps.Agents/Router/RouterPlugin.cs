using System.ComponentModel;
using Microsoft.SemanticKernel;
using HiveOps.Domain.Enums;
using HiveOps.Infrastructure.AI;

namespace HiveOps.Agents.Router;

/// <summary>
/// RouterPlugin — the first agent that sees every message.
/// Classifies user intent so the Orchestrator knows which plugin to invoke next.
/// </summary>
public sealed class RouterPlugin
{
    [KernelFunction("classify_intent")]
    [Description("Classifies the user message into an intent category. Returns one of: Greeting, HumanHandoff, IncidentReport, IncidentQuery, IncidentApprove, IncidentReject, DeployRequest or Unknown.")]
    public async Task<string> ClassifyIntentAsync(
        Kernel kernel,
        [Description("The raw user message to classify.")] string userMessage,
        CancellationToken cancellationToken = default)
    {
        var heuristic = TryClassifyWithHeuristics(userMessage);
        if (heuristic is not null)
            return heuristic.Value.ToString();

        var prompt = string.Format(
            @"Eres el router de un chatbot de soporte técnico.
Clasifica el siguiente mensaje del usuario en EXACTAMENTE UNA de estas categorías:

- Greeting: El usuario saluda, se presenta o envía un mensaje introductorio sin consulta específica.
- IncidentReport: El usuario reporta un bug, error, problema de base de datos o incidente de soporte.
- IncidentQuery: El usuario pregunta por el estado de un ticket de soporte o incidente anterior.
- IncidentApprove: El usuario aprueba una corrección propuesta por el agente de soporte.
- IncidentReject: El usuario rechaza una corrección propuesta.
- DeployRequest: El usuario solicita deployar una corrección.
- HumanHandoff: El usuario pide hablar con una persona, asesor o ingeniero.
- Unknown: La intención es ambigua o no relacionada con las categorías anteriores.

Ejemplos:
- ""Hola"" -> Greeting
- ""Hay un bug en el bot"" -> IncidentReport
- ""Quiero saber como va mi ticket"" -> IncidentQuery
- ""Apruebo el fix"" -> IncidentApprove
- ""Deployea la correccion"" -> DeployRequest
- ""Pasame con un humano"" -> HumanHandoff

Responde SOLO con el nombre de la categoría, sin explicación.

Mensaje: {0}

Categoría:", userMessage);

        string raw;
        try
        {
            var result = await kernel.InvokePromptAsync(prompt, cancellationToken: cancellationToken);
            raw = result.GetValue<string>()?.Trim() ?? "Unknown";
        }
        catch
        {
            // Never throw from routing: fall back to Unknown so the orchestrator can reply safely.
            return IntentType.Unknown.ToString();
        }

        return Enum.TryParse<IntentType>(raw, ignoreCase: true, out var parsed)
            ? parsed.ToString()
            : IntentType.Unknown.ToString();
    }

    private static IntentType? TryClassifyWithHeuristics(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return IntentType.Greeting;

        var text = userMessage.Trim().ToLowerInvariant();

        if (text == "[mensaje sin contenido de texto]")
            return IntentType.Greeting;

        // Saludos — detectados sin necesidad de llamada al LLM
        if (IsGreeting(text))
            return IntentType.Greeting;

        // Pase a humano explícito
        if (ContainsAny(text, "hablar con", "persona", "humano", "asesor", "vendedor", "agente", "operador"))
            return IntentType.HumanHandoff;

        // Incidentes de soporte
        if (ContainsAny(text, "bug", "error", "incidente", "ticket", "falla", "fallo", "problema", "crash", "excepcion", "stack trace",
            "base de datos", "bd", "database", "sql", "tabla", "columna", "deploy", "desplegar", "corregir", "fix", "patch"))
            return IntentType.IncidentReport;

        if (ContainsAny(text, "estado del ticket", "como va mi ticket", "seguimiento", "status del incidente"))
            return IntentType.IncidentQuery;

        if (ContainsAny(text, "apruebo", "confirmo fix", "ok para deploy", "dale deploy", "deployear", "desplegar"))
            return IntentType.IncidentApprove;

        if (ContainsAny(text, "rechazo", "no apruebo", "cancelar fix", "descartar"))
            return IntentType.IncidentReject;

        return null;
    }

    private static bool IsGreeting(string text)
    {
        // Saludo exacto o que empieza con saludo seguido de puntuación/espacio
        var greetings = new[] { "hola", "buenas", "buen dia", "buenos dias", "buenas tardes", "buenas noches",
            "hey", "hi", "hello", "saludos", "ey", "que tal", "como estas", "buen dia", "buendia" };
        return greetings.Any(g => text == g || text.StartsWith(g + " ") || text.StartsWith(g + ",") || text.StartsWith(g + "!") || text.StartsWith(g + "."));
    }

    private static bool ContainsAny(string text, params string[] tokens)
    {
        return tokens.Any(text.Contains);
    }
}
