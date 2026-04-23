using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Agents.Orchestration;

public sealed class AgentRulesEngine
{
    private const string LastInventoryQueryKey = "last_inventory_query";
    private const string LastCategoryKey = "last_category";
    private const string LastSportKey = "last_sport";
    private const string LastFiltersKey = "last_filters";
    private const string LastSortKey = "last_sort";
    private const string ShippingAddressPendingKey = "shipping_address_pending";
    private const string ShippingAddressKey = "shipping_address";
    private const string GuidedChoiceStageKey = "guided_choice_stage";
    private const string GuidedChoiceSportKey = "guided_choice_sport";
    private const string GuidedChoiceGoalKey = "guided_choice_goal";
    private const string GuidedChoiceBudgetKey = "guided_choice_budget";

    private readonly AppDbContext _db;
    private readonly IConversationStateManager _stateManager;

    public AgentRulesEngine(AppDbContext db, IConversationStateManager stateManager)
    {
        _db = db;
        _stateManager = stateManager;
    }

    public async Task<RuleEngineResult> ExecuteInteractiveActionAsync(
        Kernel kernel,
        ConversationStateContext state,
        string actionId,
        IncomingMessage msg,
        Conversation conversation,
        CancellationToken ct)
    {
        var normalized = actionId.Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "btn_view_cart":
            {
                var response = await kernel.InvokeAsync<string>("CommercialPlugin", "view_cart",
                    new KernelArguments { ["conversationId"] = conversation.Id.ToString() }, ct);
                return RuleEngineResult.CreateHandled(response ?? string.Empty,
                    buttons:
                    [
                        new InteractiveButtonOption("btn_view_cart", "Ver carrito"),
                        new InteractiveButtonOption("btn_buy_now", "Comprar ahora"),
                        new InteractiveButtonOption("btn_open_menu", "Menu")
                    ]);
            }

            case "btn_buy_now":
            {
                var response = await kernel.InvokeAsync<string>("CommercialPlugin", "checkout_cart",
                    new KernelArguments
                    {
                        ["conversationId"] = conversation.Id.ToString(),
                        ["customerPhone"] = msg.PhoneNumber ?? "unknown"
                    }, ct);
                return RuleEngineResult.CreateHandled(response ?? string.Empty);
            }

            case "btn_open_menu":
                return RuleEngineResult.CreateHandled("Te dejo el menú rápido para que elijas una acción.", menuSections: BuildMainMenuSections());

            case "menu_offers_bestsellers":
                return RuleEngineResult.CreateHandled(await BuildOffersOrBestSellersResponseAsync(conversation.TenantId, ct));

            case "menu_shipping":
                state.FlowData[ShippingAddressPendingKey] = bool.TrueString;
                await _stateManager.SetStateAsync(conversation.TenantId, conversation.Id, state, ct);
                return RuleEngineResult.CreateHandled("¿A qué dirección querés calcular el envío?");

            case "menu_payments":
            {
                var response = await kernel.InvokeAsync<string>("StaticInfoPlugin", "get_business_info",
                    new KernelArguments { ["infoType"] = "payments" }, ct);
                return RuleEngineResult.CreateHandled(response ?? string.Empty);
            }

            case "menu_track_order":
                return RuleEngineResult.CreateHandled(await BuildTrackOrderResponseAsync(kernel, conversation, ct));

            case "menu_help_choose":
                ResetGuidedChoice(state);
                state.FlowData[GuidedChoiceStageKey] = "sport";
                await _stateManager.SetStateAsync(conversation.TenantId, conversation.Id, state, ct);
                return RuleEngineResult.CreateHandled("Claro. ¿Para qué deporte estás buscando?");

            default:
                return RuleEngineResult.NotHandled();
        }
    }

    public async Task<RuleEngineResult> TryHandleAsync(
        Kernel kernel,
        ConversationStateContext state,
        IncomingMessage msg,
        Conversation conversation,
        Guid tenantId,
        CancellationToken ct)
    {
        var text = msg.Text?.Trim() ?? string.Empty;

        if (IsMenuRequest(text))
            return RuleEngineResult.CreateHandled("Te dejo el menú rápido para que elijas una acción.", menuSections: BuildMainMenuSections());

        if (TryMapMenuTextToAction(text, out var actionId))
            return await ExecuteInteractiveActionAsync(kernel, state, actionId, msg, conversation, ct);

        if (state.FlowData.TryGetValue(ShippingAddressPendingKey, out var pendingShipping)
            && bool.TryParse(pendingShipping, out var isPendingShipping)
            && isPendingShipping
            && LooksLikeAddressInput(text))
        {
            state.FlowData[ShippingAddressPendingKey] = bool.FalseString;
            state.FlowData[ShippingAddressKey] = text;
            await _stateManager.SetStateAsync(tenantId, conversation.Id, state, ct);
            return RuleEngineResult.CreateHandled($"Perfecto, tomo esta dirección: {text}. Cuando configures las zonas de envío en el dashboard voy a poder calcularlo automáticamente.");
        }

        if (IsHelpChooseTrigger(text)
            || state.FlowData.ContainsKey(GuidedChoiceStageKey))
        {
            var response = await HandleGuidedChoiceAsync(state, text, tenantId, conversation.Id, ct);
            if (!string.IsNullOrWhiteSpace(response))
                return RuleEngineResult.CreateHandled(response);
        }

        if (TryBuildContextualInventoryQuery(state, text, out var contextualQuery))
        {
            var response = await kernel.InvokeAsync<string>(
                "InventoryPlugin",
                "search_products",
                new KernelArguments { ["query"] = contextualQuery },
                ct);
            RememberInventoryContext(state, contextualQuery);
            await _stateManager.SetStateAsync(tenantId, conversation.Id, state, ct);
            return RuleEngineResult.CreateHandled(response ?? string.Empty);
        }

        return RuleEngineResult.NotHandled();
    }

    public void RememberInventoryContext(ConversationStateContext state, string query)
    {
        var normalized = NormalizeForRules(query);
        state.State = ConversationState.InInventoryQuery;
        state.FlowData[LastInventoryQueryKey] = query.Trim();
        state.FlowData[LastFiltersKey] = query.Trim();

        if (ContainsAny(normalized, "zapa", "zapas", "zapatilla", "zapatillas", "calzado"))
            state.FlowData[LastCategoryKey] = "zapatillas";
        else if (ContainsAny(normalized, "botin", "botines"))
            state.FlowData[LastCategoryKey] = "botines";
        else if (ContainsAny(normalized, "remera", "short", "camiseta", "ropa"))
            state.FlowData[LastCategoryKey] = "ropa";

        if (ContainsAny(normalized, "futbol", "fútbol", "football", "fulbo"))
            state.FlowData[LastSportKey] = "futbol";
        else if (ContainsAny(normalized, "running", "correr"))
            state.FlowData[LastSportKey] = "running";
        else if (ContainsAny(normalized, "tenis", "tennis"))
            state.FlowData[LastSportKey] = "tenis";
        else if (ContainsAny(normalized, "natacion", "nadar", "swim"))
            state.FlowData[LastSportKey] = "natacion";

        if (ContainsAny(normalized, "lo mas barato", "más barato", "mas barato", "barato", "economico", "económico"))
            state.FlowData[LastSortKey] = "price_asc";
    }

    public static List<InteractiveListSection> BuildMainMenuSections()
    {
        return
        [
            new InteractiveListSection(
                "Acciones",
                [
                    new InteractiveListRow("menu_offers_bestsellers", "Ofertas / mas vendidos"),
                    new InteractiveListRow("menu_shipping", "Calcular envio"),
                    new InteractiveListRow("menu_payments", "Ver medios de pago"),
                    new InteractiveListRow("menu_track_order", "Seguir pedido"),
                    new InteractiveListRow("menu_help_choose", "Ayudame a elegir")
                ])
        ];
    }

    private async Task<string> BuildOffersOrBestSellersResponseAsync(Guid tenantId, CancellationToken ct)
    {
        var configuredOffers = await GetConfiguredOffersAsync(tenantId, ct);
        if (configuredOffers.Count > 0)
            return BuildBulletedResponse("🔥 Estas son las ofertas activas:", configuredOffers);

        var bestSellers = await _db.OrderItems
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId)
            .GroupBy(i => new { i.ProductName, i.UnitPrice })
            .Select(g => new { g.Key.ProductName, g.Key.UnitPrice, Units = g.Sum(x => x.Quantity) })
            .OrderByDescending(x => x.Units)
            .ThenBy(x => x.UnitPrice)
            .Take(5)
            .ToListAsync(ct);

        if (bestSellers.Count > 0)
        {
            var lines = bestSellers.Select(x => $"*{x.ProductName}* — {x.Units} vendido(s) — ${x.UnitPrice:N0}").ToList();
            return BuildBulletedResponse("🔥 Te muestro los más vendidos de este momento:", lines);
        }

        var featured = await _db.Products
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId)
            .OrderByDescending(p => p.StockQuantity)
            .ThenBy(p => p.Price)
            .Take(5)
            .Select(p => $"*{p.Name}* — {p.Brand} — ${p.Price:N0}")
            .ToListAsync(ct);

        return BuildBulletedResponse("🔥 No hay ofertas configuradas, pero te dejo productos destacados:", featured);
    }

    private async Task<List<string>> GetConfiguredOffersAsync(Guid tenantId, CancellationToken ct)
    {
        var tenantConfigJson = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.ConfigJson)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(tenantConfigJson))
            return [];

        try
        {
            using var document = JsonDocument.Parse(tenantConfigJson);
            var offers = new List<string>();
            if (TryReadStringArray(document.RootElement, offers, "offers", "promotions", "featuredOffers"))
                return offers;
        }
        catch
        {
        }

        return [];
    }

    private async Task<string> BuildTrackOrderResponseAsync(Kernel kernel, Conversation conversation, CancellationToken ct)
    {
        var hasConfirmedOrder = await _db.Orders
            .AsNoTracking()
            .AnyAsync(o => o.ConversationId == conversation.Id && o.Status != OrderStatus.Draft, ct);

        if (!hasConfirmedOrder)
            return "Pasame tu número de pedido o algún dato de la compra y lo reviso.";

        var response = await kernel.InvokeAsync<string>(
            "CommercialPlugin",
            "track_order",
            new KernelArguments { ["conversationId"] = conversation.Id.ToString() },
            ct);

        return response ?? string.Empty;
    }

    private async Task<string?> HandleGuidedChoiceAsync(
        ConversationStateContext state,
        string userText,
        Guid tenantId,
        Guid conversationId,
        CancellationToken ct)
    {
        if (IsHelpChooseTrigger(userText))
        {
            ResetGuidedChoice(state);
            state.FlowData[GuidedChoiceStageKey] = "sport";
            await _stateManager.SetStateAsync(tenantId, conversationId, state, ct);
            return "Claro. ¿Para qué deporte estás buscando?";
        }

        if (!state.FlowData.TryGetValue(GuidedChoiceStageKey, out var stage))
            return null;

        switch (stage)
        {
            case "sport":
            {
                var sportText = userText.Trim();
                var hasProducts = await _db.Products
                    .AsNoTracking()
                    .AnyAsync(p => p.TenantId == tenantId
                        && (p.Category.Contains(sportText)
                            || p.Tags.Contains(sportText)
                            || p.Name.Contains(sportText)), ct);

                if (!hasProducts)
                {
                    ResetGuidedChoice(state);
                    await _stateManager.SetStateAsync(tenantId, conversationId, state, ct);
                    return $"No encontré productos para \"{sportText}\". ¿Querés que busque algo diferente o te cuento qué categorías tenemos?";
                }

                state.FlowData[GuidedChoiceSportKey] = sportText;
                state.FlowData[GuidedChoiceStageKey] = "goal";
                await _stateManager.SetStateAsync(tenantId, conversationId, state, ct);
                return "Perfecto. ¿Qué objetivo tenés: competir, entrenar, arrancar de cero o algo más tranqui?";
            }

            case "goal":
                state.FlowData[GuidedChoiceGoalKey] = userText;
                state.FlowData[GuidedChoiceStageKey] = "budget";
                await _stateManager.SetStateAsync(tenantId, conversationId, state, ct);
                return "Bien. ¿Qué presupuesto querés manejar?";

            case "budget":
                state.FlowData[GuidedChoiceBudgetKey] = userText;
                state.FlowData[GuidedChoiceStageKey] = "complete";
                state.FlowData[LastSportKey] = state.FlowData[GuidedChoiceSportKey];
                await _stateManager.SetStateAsync(tenantId, conversationId, state, ct);
                return $"Perfecto. Entonces te oriento para {state.FlowData[GuidedChoiceSportKey]}, con objetivo {state.FlowData[GuidedChoiceGoalKey]} y presupuesto {state.FlowData[GuidedChoiceBudgetKey]}. Si querés ver opciones, decime \"mostrame opciones\".";

            case "complete":
                if (LooksLikeShowOptions(userText))
                {
                    var composedQuery = $"{state.FlowData.GetValueOrDefault(GuidedChoiceSportKey, string.Empty)} {state.FlowData.GetValueOrDefault(GuidedChoiceGoalKey, string.Empty)} {state.FlowData.GetValueOrDefault(GuidedChoiceBudgetKey, string.Empty)}".Trim();
                    state.FlowData[LastInventoryQueryKey] = composedQuery;
                    state.FlowData[LastFiltersKey] = composedQuery;
                    await _stateManager.SetStateAsync(tenantId, conversationId, state, ct);
                    return null;
                }

                // Any other message: abandon guided flow and let it fall through to normal processing
                ResetGuidedChoice(state);
                await _stateManager.SetStateAsync(tenantId, conversationId, state, ct);
                return null;

            default:
                return null;
        }
    }

    private static bool TryBuildContextualInventoryQuery(ConversationStateContext state, string userText, out string contextualQuery)
    {
        contextualQuery = string.Empty;
        var normalized = NormalizeForRules(userText);

        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (state.FlowData.TryGetValue(GuidedChoiceStageKey, out var guidedStage)
            && string.Equals(guidedStage, "complete", StringComparison.OrdinalIgnoreCase)
            && LooksLikeShowOptions(userText))
        {
            contextualQuery = $"{state.FlowData.GetValueOrDefault(GuidedChoiceSportKey, string.Empty)} {state.FlowData.GetValueOrDefault(GuidedChoiceGoalKey, string.Empty)} {state.FlowData.GetValueOrDefault(GuidedChoiceBudgetKey, string.Empty)}".Trim();
            return !string.IsNullOrWhiteSpace(contextualQuery);
        }

        if (!state.FlowData.TryGetValue(LastInventoryQueryKey, out var lastQuery) || string.IsNullOrWhiteSpace(lastQuery))
            return false;

        if (IsRefinementMessage(normalized))
        {
            contextualQuery = $"{lastQuery} {userText}".Trim();
            return true;
        }

        return false;
    }

    private static bool IsRefinementMessage(string normalized)
    {
        if (IsMenuRequest(normalized)
            || IsHelpChooseTrigger(normalized)
            || normalized is "si" or "sí" or "no" or "ok" or "gracias")
            return false;

        if (ContainsAny(normalized,
            "lo mas barato",
            "más barato",
            "mas barato",
            "mas economico",
            "más económico",
            "nike",
            "adidas",
            "puma",
            "under armour",
            "negro",
            "blanco",
            "azul",
            "rojo",
            "verde"))
            return true;

        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length <= 4;
    }

    private static void ResetGuidedChoice(ConversationStateContext state)
    {
        state.FlowData.Remove(GuidedChoiceStageKey);
        state.FlowData.Remove(GuidedChoiceSportKey);
        state.FlowData.Remove(GuidedChoiceGoalKey);
        state.FlowData.Remove(GuidedChoiceBudgetKey);
    }

    private static bool IsMenuRequest(string text)
    {
        var normalized = NormalizeForRules(text);
        return normalized == "menu"
            || normalized == "menú"
            || normalized == "opciones"
            || normalized.Contains("ver opciones", StringComparison.Ordinal)
            || normalized.Contains("mostrar opciones", StringComparison.Ordinal);
    }

    private static bool TryMapMenuTextToAction(string text, out string actionId)
    {
        var normalized = NormalizeForRules(text);

        if (normalized.Contains("ofertas", StringComparison.Ordinal) || normalized.Contains("mas vendidos", StringComparison.Ordinal) || normalized.Contains("más vendidos", StringComparison.Ordinal))
        {
            actionId = "menu_offers_bestsellers";
            return true;
        }

        if (normalized.Contains("calcular envio", StringComparison.Ordinal) || normalized.Contains("calcular envío", StringComparison.Ordinal))
        {
            actionId = "menu_shipping";
            return true;
        }

        if (normalized.Contains("medios de pago", StringComparison.Ordinal) || normalized.Contains("formas de pago", StringComparison.Ordinal))
        {
            actionId = "menu_payments";
            return true;
        }

        if (normalized.Contains("seguir pedido", StringComparison.Ordinal) || normalized.Contains("estado del pedido", StringComparison.Ordinal))
        {
            actionId = "menu_track_order";
            return true;
        }

        if (normalized.Contains("ayudame a elegir", StringComparison.Ordinal) || normalized.Contains("ayudarme a elegir", StringComparison.Ordinal))
        {
            actionId = "menu_help_choose";
            return true;
        }

        actionId = string.Empty;
        return false;
    }

    private static bool IsHelpChooseTrigger(string text)
    {
        var normalized = NormalizeForRules(text);
        return normalized.Contains("ayudame a elegir", StringComparison.Ordinal)
            || normalized.Contains("ayudarme a elegir", StringComparison.Ordinal)
            || normalized.Contains("necesito ayuda para elegir", StringComparison.Ordinal);
    }

    private static bool LooksLikeShowOptions(string userText)
    {
        var normalized = NormalizeForRules(userText);
        return normalized.Contains("mostrame opciones", StringComparison.Ordinal)
            || normalized.Contains("mostrame", StringComparison.Ordinal)
            || normalized.Contains("mostrar opciones", StringComparison.Ordinal)
            || normalized.Contains("ver opciones", StringComparison.Ordinal);
    }

    private static bool LooksLikeAddressInput(string userText)
    {
        var normalized = NormalizeForRules(userText);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (normalized is "no" or "no quiero" or "hola" or "menu" or "menú" or "opciones" or "gracias" or "ok")
            return false;

        return normalized.Any(char.IsDigit)
            || ContainsAny(normalized, "calle", "avenida", "av.", "av ", "pasaje", "ruta", "barrio", "altura", "piso", "depto", "departamento", "casa", "esquina");
    }

    private static string BuildBulletedResponse(string heading, IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        builder.AppendLine(heading);
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
            builder.AppendLine($"• {line}");
        return builder.ToString().TrimEnd();
    }

    private static string NormalizeForRules(string text)
        => text.Trim().ToLowerInvariant();

    private static bool ContainsAny(string text, params string[] tokens)
        => tokens.Any(text.Contains);

    private static bool TryReadStringArray(JsonElement root, List<string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryFindProperty(root, key, out var node))
                continue;

            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var value = item.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                            values.Add(value);
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        if (TryFindProperty(item, "title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String)
                        {
                            var title = titleNode.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(title))
                                values.Add(title);
                        }
                    }
                }
            }
        }

        return values.Count > 0;
    }

    private static bool TryFindProperty(JsonElement element, string key, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }

                if (TryFindProperty(property.Value, key, out value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (TryFindProperty(child, key, out value))
                    return true;
            }
        }

        value = default;
        return false;
    }
}

public sealed record RuleEngineResult(
    bool Handled,
    string Response,
    List<InteractiveButtonOption>? Buttons,
    List<InteractiveListSection>? MenuSections)
{
    public static RuleEngineResult NotHandled() => new(false, string.Empty, null, null);

    public static RuleEngineResult CreateHandled(
        string response,
        List<InteractiveButtonOption>? buttons = null,
        List<InteractiveListSection>? menuSections = null) =>
        new(true, response, buttons, menuSections);
}
