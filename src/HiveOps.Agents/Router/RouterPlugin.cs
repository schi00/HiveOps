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
    [Description("Classifies the user message into an intent category. Returns one of: Greeting, Purchase, Inventory, Reservation, StaticInfo, HumanHandoff, IncidentReport, IncidentQuery, IncidentApprove, IncidentReject, DeployRequest or Unknown.")]
    public async Task<string> ClassifyIntentAsync(
        Kernel kernel,
        [Description("The raw user message to classify.")] string userMessage,
        CancellationToken cancellationToken = default)
    {
        var heuristic = TryClassifyWithHeuristics(userMessage);
        if (heuristic is not null)
            return heuristic.Value.ToString();

        var prompt = $"""
            Eres el router de un chatbot de atención al cliente para una tienda deportiva.
            Clasifica el siguiente mensaje del cliente en EXACTAMENTE UNA de estas categorías:

            - Greeting: El cliente saluda, se presenta o envía un mensaje introductorio sin consulta específica.
            - Purchase: El cliente quiere comprar, agregar al carrito o hacer el checkout.
            - Inventory: El cliente pregunta por disponibilidad de productos, stock, marcas o busca un artículo.
            - Reservation: El cliente quiere hacer, modificar o cancelar una reserva o turno.
            - StaticInfo: El cliente pregunta por horarios, sucursales, envios o política de devoluciones.
            - IncidentReport: El cliente reporta un bug, error, problema de base de datos o incidente de soporte.
            - IncidentQuery: El cliente pregunta por el estado de un ticket de soporte o incidente anterior.
            - IncidentApprove: El cliente aprueba una corrección propuesta por el agente de soporte.
            - IncidentReject: El cliente rechaza una corrección propuesta.
            - DeployRequest: El cliente solicita deployar una corrección.
            - Unknown: La intención es ambigua o no relacionada con las categorías anteriores.

            Ejemplos:
            - "Hola" -> Greeting
            - "Buenas tardes" -> Greeting
            - "Tienen zapatillas Nike en talla 42?" -> Inventory
            - "Quiero comprar dos remeras" -> Purchase
            - "Reservame para hoy a las 21" -> Reservation
            - "Cuales son los horarios de atencion?" -> StaticInfo
            - "Hay un bug en el bot" -> IncidentReport
            - "Quiero saber como va mi ticket" -> IncidentQuery
            - "Apruebo el fix" -> IncidentApprove
            - "Deployea la correccion" -> DeployRequest
            - "Pasame con un humano" -> Unknown

            Responde SOLO con el nombre de la categoría, sin explicación.

            Mensaje: {userMessage}

            Categoría:
            """;

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

        // Compra
        if (ContainsAny(text, "compr", "carrito", "checkout", "pagar", "pedido", "orden", "quiero uno", "quiero dos", "dame"))
            return IntentType.Purchase;

        // Info estática
        if (ContainsAny(text,
            "horario", "sucursal", "direcci", "envio", "envío", "devolucion", "devolución", "politica", "política", "retiro", "local", "abierto", "cierra",
            "dias abiertos", "días abiertos", "dias atienden", "días atienden", "abren",
            "medios de pago", "formas de pago", "pago", "pagos", "tarjeta", "efectivo", "transferencia", "mercado pago"))
            return IntentType.StaticInfo;

        // Inventario — ampliado con verbos de consulta + categorías de productos y deportes
        if (ContainsAny(text,
            "stock", "disponible", "inventario", "sku", "talla", "color", "marca",
            "tienen", "tiene", "busco", "buscar", "busca", "necesito", "quiero ver",
            "hay ", "muestrame", "mostrame", "mostrar", "catalogo", "catálogo",
            "zapatilla", "zapatillas", "zapa", "zapas", "remera", "short", "camiseta", "mochila", "pelota", "botin", "botines",
            "futbol", "fútbol", "football", "footbol", "fulbo", "soccer", "running", "correr", "basquet", "basket", "basketball",
            "natacion", "nadar", "swim", "voley", "voleibol", "yoga", "pilates", "ciclismo", "padel", "boxeo", "trekking", "hiking", "handball", "rugby",
            "auriculares", "cinta", "equipamiento", "ropa", "calzado", "accesorio",
            "nike", "adidas", "puma", "under armour", "jbl", "domyos",
            "precio", "costo", "cuanto sale", "cuanto cuesta",
            "producto", "productos", "marcas", "categorias", "categorías", "tipo de producto", "tipos de productos",
            "cosas para", "articulos de", "articulos para", "algo de", "para practicar", "para jugar"))
            return IntentType.Inventory;

        if (ContainsAny(text, "jugar", "deporte", "entrenar")
            && ContainsAny(text, "quiero", "busco", "algo"))
            return IntentType.Inventory;

        // Reserva
        if (ContainsAny(text, "reserv", "turno", "agenda", "mesa", "cita"))
            return IntentType.Reservation;

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
