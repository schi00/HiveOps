using SaaSBot.Domain.Enums;

namespace SaaSBot.Agents.Planning;

public static class AgentToolCatalog
{
    public static IReadOnlyList<AgentToolDefinition> GetDefaultDefinitions()
    {
        return
        [
            new AgentToolDefinition
            {
                Name = "search_products",
                Description = "Finds products from partial or complete filters. Primary discovery tool before recommendation or purchase actions.",
                WhenToUse =
                [
                    "User asks to find, browse, discover, compare, or recommend products.",
                    "User provides partial attributes like brand, color, size, category, or budget.",
                    "Agent needs data-backed options before proposing next step."
                ],
                WhenNotToUse =
                [
                    "User asks details for a known product id (use get_product_details).",
                    "User asks store policies/hours/shipping/payments (use get_store_info).",
                    "User asks order tracking (use get_order_status)."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["query"] = new() { Type = "string", Required = false, Description = "Natural language search phrase.", Example = "zapatillas negras para correr" },
                    ["category"] = new() { Type = "string", Required = false, Description = "Product category.", Example = "calzado" },
                    ["brand"] = new() { Type = "string", Required = false, Description = "Brand filter.", Example = "Nike" },
                    ["color"] = new() { Type = "string", Required = false, Description = "Color filter.", Example = "negro" },
                    ["size"] = new() { Type = "string", Required = false, Description = "Size filter.", Example = "42" },
                    ["min_price"] = new() { Type = "number", Required = false, Description = "Minimum price.", Example = "30000" },
                    ["max_price"] = new() { Type = "number", Required = false, Description = "Maximum price.", Example = "120000" },
                    ["top_k"] = new() { Type = "number", Required = false, Description = "Maximum number of results.", Example = "5" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Busco zapatillas negras talle 42",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["query"] = "zapatillas negras talle 42",
                            ["category"] = "calzado",
                            ["color"] = "negro",
                            ["size"] = "42",
                            ["top_k"] = "5"
                        }
                    }
                ],
                AllowedStates = [ConversationState.Idle, ConversationState.InInventoryQuery, ConversationState.InOrderFlow, ConversationState.Completed]
            },
            new AgentToolDefinition
            {
                Name = "get_product_details",
                Description = "Returns authoritative details for a specific product (price, stock, SKU and basic attributes).",
                WhenToUse =
                [
                    "User refers to a known product or asks specific product details.",
                    "Agent must verify stock/price before add_to_cart."
                ],
                WhenNotToUse =
                [
                    "User is broadly exploring options (use search_products first).",
                    "User asks non-product policy/order questions."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["product_id"] = new() { Type = "string", Required = true, Description = "Product identifier from previous context.", Example = "a3f5c7e1-9d2b-4a0f-8df3-4f1cd64b21ab" },
                    ["include_variants"] = new() { Type = "boolean", Required = false, Description = "Include variant hints if available.", Example = "true" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "La primera tiene stock?",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["product_id"] = "from_previous_search_result_1",
                            ["include_variants"] = "true"
                        }
                    }
                ],
                AllowedStates = [ConversationState.Idle, ConversationState.InInventoryQuery, ConversationState.InOrderFlow, ConversationState.Completed]
            },
            new AgentToolDefinition
            {
                Name = "add_to_cart",
                Description = "Adds a resolved product to cart with quantity and optional variant.",
                WhenToUse =
                [
                    "User confirms they want a specific product.",
                    "Product id is already resolved from context/search."
                ],
                WhenNotToUse =
                [
                    "Product is ambiguous or unknown.",
                    "User only asks informational questions."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["product_id"] = new() { Type = "string", Required = true, Description = "Resolved product id.", Example = "a3f5c7e1-9d2b-4a0f-8df3-4f1cd64b21ab" },
                    ["quantity"] = new() { Type = "number", Required = true, Description = "Units to add.", Example = "1" },
                    ["variant"] = new() { Type = "string", Required = false, Description = "Variant descriptor.", Example = "talle 42 negro" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Agregame 2 de esas",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["product_id"] = "from_previous_selection",
                            ["quantity"] = "2"
                        }
                    }
                ],
                AllowedStates = [ConversationState.InOrderFlow]
            },
            new AgentToolDefinition
            {
                Name = "view_cart",
                Description = "Shows current cart contents and totals to continue conversion or checkout.",
                WhenToUse =
                [
                    "User asks to review cart.",
                    "Agent needs cart state before checkout."
                ],
                WhenNotToUse =
                [
                    "User is still discovering products.",
                    "No purchase/cart intent exists."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["include_pricing"] = new() { Type = "boolean", Required = false, Description = "Include totals if available.", Example = "true" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Mostrame el carrito",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["include_pricing"] = "true"
                        }
                    }
                ],
                AllowedStates = [ConversationState.InOrderFlow, ConversationState.Completed]
            },
            new AgentToolDefinition
            {
                Name = "start_checkout",
                Description = "Starts checkout for active cart when user is ready to buy.",
                WhenToUse =
                [
                    "User confirms purchase intent.",
                    "Cart is expected to contain items."
                ],
                WhenNotToUse =
                [
                    "User is still comparing products.",
                    "Cart is empty or unknown (use view_cart first)."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["confirm_purchase"] = new() { Type = "boolean", Required = true, Description = "Explicit go-ahead to checkout.", Example = "true" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Quiero comprar ahora",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["confirm_purchase"] = "true"
                        }
                    }
                ],
                AllowedStates = [ConversationState.InOrderFlow]
            },
            new AgentToolDefinition
            {
                Name = "get_order_status",
                Description = "Retrieves latest order status for tracking and post-sale support.",
                WhenToUse =
                [
                    "User asks where is my order or status update.",
                    "User shares order id."
                ],
                WhenNotToUse =
                [
                    "User has not completed checkout yet.",
                    "User asks inventory or policy details."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["order_id"] = new() { Type = "string", Required = false, Description = "Specific order id if user provides it.", Example = "ORD-2026-000154" },
                    ["use_latest_for_conversation"] = new() { Type = "boolean", Required = false, Description = "Use latest order in conversation when order_id is absent.", Example = "true" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Donde esta mi pedido?",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["use_latest_for_conversation"] = "true"
                        }
                    }
                ],
                AllowedStates = [ConversationState.Idle, ConversationState.InOrderFlow, ConversationState.Completed]
            },
            new AgentToolDefinition
            {
                Name = "get_store_info",
                Description = "Gets business information such as hours, branches, shipping, returns, and payment methods.",
                WhenToUse =
                [
                    "User asks operational store information.",
                    "Agent needs factual policy response."
                ],
                WhenNotToUse =
                [
                    "User asks product discovery or cart operations.",
                    "User asks order tracking."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["info_type"] = new() { Type = "string", Required = true, Description = "Business info type.", Example = "shipping" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Que medios de pago tienen?",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["info_type"] = "payments"
                        }
                    }
                ],
                AllowedStates = [ConversationState.Idle, ConversationState.InInventoryQuery, ConversationState.InOrderFlow, ConversationState.InReservationFlow, ConversationState.Completed]
            },
            new AgentToolDefinition
            {
                Name = "escalate_to_human",
                Description = "Requests human handoff when automation should stop due to frustration, explicit request, or out-of-scope need.",
                WhenToUse =
                [
                    "User explicitly asks for human support.",
                    "Repeated misunderstandings or frustration signals.",
                    "Out-of-scope request for automated flow."
                ],
                WhenNotToUse =
                [
                    "A valid domain tool can still solve user intent safely.",
                    "Single low-confidence turn where clarification is enough."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["reason"] = new() { Type = "string", Required = true, Description = "Operational reason for escalation.", Example = "User explicitly requested human support" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Quiero hablar con una persona",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["reason"] = "Explicit human handoff request"
                        }
                    }
                ],
                AllowedStates = [ConversationState.Idle, ConversationState.InInventoryQuery, ConversationState.InOrderFlow, ConversationState.InReservationFlow, ConversationState.Completed]
            }
        ];
    }
}
