using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Agents.Commercial;

/// <summary>
/// CommercialPlugin — Stateful agent that manages reservations and orders.
/// Reads/writes conversation state via IConversationStateManager (Redis).
/// Drives the user towards conversion by collecting missing data iteratively.
/// </summary>
public sealed class CommercialPlugin
{
    private readonly AppDbContext _db;
    private readonly IConversationStateManager _stateManager;

    public CommercialPlugin(AppDbContext db, IConversationStateManager stateManager)
    {
        _db = db;
        _stateManager = stateManager;
    }

    // ─── Reservations ────────────────────────────────────────────────────────

    [KernelFunction("start_reservation")]
    [Description("Starts a reservation flow. Call this when the user expresses intent to make a booking.")]
    public async Task<string> StartReservationAsync(
        Kernel kernel,
        [Description("The conversation ID (GUID).")] string conversationId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var ctx = await GetOrCreateStateAsync(tenantId, convId, cancellationToken);
        ctx.State = ConversationState.InReservationFlow;
        ctx.FlowData.Clear();
        await _stateManager.SetStateAsync(tenantId, convId, ctx, cancellationToken);

        return "Reservation flow started. Please ask the customer for: party size, desired date/time, and their name.";
    }

    [KernelFunction("collect_reservation_data")]
    [Description("Stores partial reservation data collected from the user. Returns what data is still missing.")]
    public async Task<string> CollectReservationDataAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("Customer's name if provided.")] string? customerName = null,
        [Description("Desired reservation time as ISO 8601 string.")] string? reservationTime = null,
        [Description("Number of people in the party.")] int? partySize = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var ctx = await GetOrCreateStateAsync(tenantId, convId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(customerName)) ctx.FlowData["customerName"] = customerName;
        if (!string.IsNullOrWhiteSpace(reservationTime)) ctx.FlowData["reservationTime"] = reservationTime;
        if (partySize.HasValue) ctx.FlowData["partySize"] = partySize.Value.ToString();

        await _stateManager.SetStateAsync(tenantId, convId, ctx, cancellationToken);

        var missing = new List<string>();
        if (!ctx.FlowData.ContainsKey("customerName")) missing.Add("customer name");
        if (!ctx.FlowData.ContainsKey("reservationTime")) missing.Add("date and time");
        if (!ctx.FlowData.ContainsKey("partySize")) missing.Add("party size");

        return missing.Count == 0
            ? "All data collected. Call confirm_reservation to proceed."
            : $"Still missing: {string.Join(", ", missing)}.";
    }

    [KernelFunction("confirm_reservation")]
    [Description("Confirms a reservation if all required data is present and a slot is available. Returns confirmation details or the next available slot.")]
    public async Task<string> ConfirmReservationAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("Phone number of the customer.")] string customerPhone,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var ctx = await GetOrCreateStateAsync(tenantId, convId, cancellationToken);

        if (!ctx.FlowData.TryGetValue("reservationTime", out var timeStr) ||
            !DateTimeOffset.TryParse(timeStr, out var reservationTime))
            return "Missing or invalid reservation time.";

        if (!ctx.FlowData.TryGetValue("partySize", out var partySizeStr) ||
            !int.TryParse(partySizeStr, out var partySize))
            return "Missing party size.";

        ctx.FlowData.TryGetValue("customerName", out var customerName);

        // Check availability (simple: count existing reservations for the same hour)
        var slotStart = reservationTime.AddMinutes(-30);
        var slotEnd = reservationTime.AddMinutes(30);

        var conflictCount = await _db.Reservations
            .CountAsync(r =>
                r.ReservationTime >= slotStart &&
                r.ReservationTime <= slotEnd &&
                r.Status == ReservationStatus.Confirmed,
                cancellationToken);

        if (conflictCount >= 10) // configurable cap
        {
            // Suggest next available 30-min slot
            var next = reservationTime.AddMinutes(30);
            return $"No availability at {reservationTime:HH:mm}. Next available slot: {next:HH:mm}. Would you like that instead?";
        }

        var token = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var reservation = new Reservation
        {
            TenantId = tenantId,
            ConversationId = convId,
            CustomerName = customerName ?? "Guest",
            CustomerPhone = customerPhone,
            PartySize = partySize,
            ReservationTime = reservationTime,
            Status = ReservationStatus.Confirmed,
            ConfirmationToken = token
        };

        _db.Reservations.Add(reservation);
        await _db.SaveChangesAsync(cancellationToken);

        ctx.State = ConversationState.Completed;
        ctx.FlowData.Clear();
        await _stateManager.SetStateAsync(tenantId, convId, ctx, cancellationToken);

        return $"Reservation confirmed! Reference: {token}. Date: {reservationTime:dddd dd MMMM 'at' HH:mm} for {partySize} {(partySize == 1 ? "person" : "people")}.";
    }

    // ─── Orders / Cart ────────────────────────────────────────────────────────

    [KernelFunction("start_order")]
    [Description("Starts a new order/cart for the customer.")]
    public async Task<string> StartOrderAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("Customer phone number.")] string customerPhone,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var ctx = await GetOrCreateStateAsync(tenantId, convId, cancellationToken);

        // Check for an existing draft order
        var existing = await _db.Orders
            .FirstOrDefaultAsync(o => o.ConversationId == convId && o.Status == OrderStatus.Draft, cancellationToken);

        if (existing is not null)
            return $"Existing cart found (Order ID: {existing.Id}). Use add_to_cart to add items.";

        var order = new Order
        {
            TenantId = tenantId,
            ConversationId = convId,
            CustomerPhone = customerPhone,
            Status = OrderStatus.Draft
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(cancellationToken);

        ctx.State = ConversationState.InOrderFlow;
        ctx.FlowData["orderId"] = order.Id.ToString();
        await _stateManager.SetStateAsync(tenantId, convId, ctx, cancellationToken);

        return $"Cart created (Order ID: {order.Id}). Add items with add_to_cart.";
    }

    [KernelFunction("add_to_cart")]
    [Description("Adds a product to the customer's current cart.")]
    public async Task<string> AddToCartAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("Product ID (GUID) to add.")] string productId,
        [Description("Quantity to add.")] int quantity = 1,
        [Description("Optional product variant (e.g. size, color).")] string? variant = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId) || !Guid.TryParse(productId, out var pid))
            return "Invalid ID format.";

        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.ConversationId == convId && o.Status == OrderStatus.Draft, cancellationToken);

        if (order is null)
            return "No active cart. Call start_order first.";

        var product = await _db.Products.FindAsync([pid], cancellationToken);
        if (product is null)
            return "Product not found.";
        if (product.StockQuantity < quantity)
            return $"Insufficient stock. Available: {product.StockQuantity}.";

        order.Items.Add(new OrderItem
        {
            TenantId = tenantId,
            OrderId = order.Id,
            ProductId = pid,
            ProductName = product.Name,
            Quantity = quantity,
            UnitPrice = product.Price,
            Variant = variant
        });

        order.TotalAmount = order.Items.Sum(i => i.UnitPrice * i.Quantity);
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return $"Added {quantity}x {product.Name} to cart. Cart total: ${order.TotalAmount:F2}.";
    }

    [KernelFunction("view_cart")]
    [Description("Returns the active cart items and totals for a conversation.")]
    public async Task<string> ViewCartAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var order = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.ConversationId == convId && o.Status == OrderStatus.Draft, cancellationToken);

        if (order is null)
            return "No tenés un carrito activo todavía. Puedo iniciar uno cuando quieras comprar.";

        if (order.Items.Count == 0)
            return "Tu carrito está vacío por ahora. Decime qué producto querés agregar.";

        var cartTotal = order.Items.Sum(i => i.Quantity * i.UnitPrice);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Este es tu carrito:");
        foreach (var item in order.Items)
        {
            var lineTotal = item.Quantity * item.UnitPrice;
            sb.AppendLine($"• {item.Quantity}x {item.ProductName} — ${lineTotal:N0}");
        }

        sb.AppendLine($"\nTotal: ${cartTotal:N0}");
        sb.Append("¿Querés comprar ahora o seguir agregando productos?");
        return sb.ToString();
    }

    [KernelFunction("checkout_cart")]
    [Description("Confirms the active draft cart and turns it into a confirmed order.")]
    public async Task<string> CheckoutCartAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("Customer phone number.")] string? customerPhone = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.ConversationId == convId && o.Status == OrderStatus.Draft, cancellationToken);

        if (order is null)
            return "No hay un carrito activo para confirmar.";

        if (order.Items.Count == 0)
            return "Tu carrito está vacío. Agregá al menos un producto para comprar.";

        if (!string.IsNullOrWhiteSpace(customerPhone) && customerPhone != "unknown")
            order.CustomerPhone = customerPhone;

        order.TotalAmount = order.Items.Sum(i => i.UnitPrice * i.Quantity);
        order.Status = OrderStatus.Confirmed;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var state = await GetOrCreateStateAsync(tenantId, convId, cancellationToken);
        state.State = ConversationState.Completed;
        state.FlowData.Clear();
        await _stateManager.SetStateAsync(tenantId, convId, state, cancellationToken);

        return $"Compra confirmada. Número de pedido: {order.Id}. Total: ${order.TotalAmount:N0}.";
    }

    [KernelFunction("track_order")]
    [Description("Returns latest non-draft order status for the conversation.")]
    public async Task<string> TrackOrderAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var order = await _db.Orders
            .AsNoTracking()
            .Where(o => o.ConversationId == convId && o.Status != OrderStatus.Draft)
            .OrderByDescending(o => o.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (order is null)
            return "Todavía no encuentro pedidos confirmados en esta conversación.";

        return $"Pedido {order.Id}: estado {order.Status}, total ${order.TotalAmount:N0}.";
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private async Task<ConversationStateContext> GetOrCreateStateAsync(
        Guid tenantId, Guid conversationId, CancellationToken ct)
    {
        return await _stateManager.GetStateAsync(tenantId, conversationId, ct)
            ?? new ConversationStateContext { TenantId = tenantId, ConversationId = conversationId };
    }

    private static Guid GetTenantId(Kernel kernel)
    {
        if (kernel.Data.TryGetValue(KernelConstants.TenantIdKey, out var val) && val is Guid g && g != Guid.Empty)
            return g;

        throw new InvalidOperationException("TenantId is required for tenant-scoped plugin execution.");
    }
}
