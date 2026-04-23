using HiveOps.Domain.Enums;

namespace HiveOps.Agents.Planning;

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
            },
            new AgentToolDefinition
            {
                Name = "analyze_incident",
                Description = "Analyzes a reported incident, classifies it, and asks for missing information if needed.",
                WhenToUse =
                [
                    "User reports a bug, error, database issue, or support incident.",
                    "User describes a technical problem with the system."
                ],
                WhenNotToUse =
                [
                    "User asks for product inventory or commercial info.",
                    "User is in a purchase or reservation flow."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["conversation_id"] = new() { Type = "string", Required = true, Description = "Conversation ID.", Example = "a3f5c7e1-9d2b-4a0f-8df3-4f1cd64b21ab" },
                    ["description"] = new() { Type = "string", Required = true, Description = "User's incident description.", Example = "Messages are being duplicated" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Hay un bug: los mensajes se duplican",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["conversation_id"] = "from_context",
                            ["description"] = "Hay un bug: los mensajes se duplican"
                        }
                    }
                ],
                AllowedStates = [ConversationState.Idle, ConversationState.InIncidentAnalysis, ConversationState.InIncidentResolution, ConversationState.Completed]
            },
            new AgentToolDefinition
            {
                Name = "query_database_diagnostic",
                Description = "Runs safe read-only SQL diagnostics (SELECT, EXPLAIN, SHOW only).",
                WhenToUse =
                [
                    "Agent needs to inspect database health or table contents for diagnosis.",
                    "User reports a database-related issue."
                ],
                WhenNotToUse =
                [
                    "User has not reported an incident.",
                    "Query is not read-only."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["conversation_id"] = new() { Type = "string", Required = true, Description = "Conversation ID.", Example = "a3f5c7e1-9d2b-4a0f-8df3-4f1cd64b21ab" },
                    ["sql_query"] = new() { Type = "string", Required = true, Description = "Safe SELECT/EXPLAIN/SHOW query.", Example = "SELECT COUNT(*) FROM Conversations" }
                },
                AllowedStates = [ConversationState.InIncidentAnalysis, ConversationState.InIncidentResolution]
            },
            new AgentToolDefinition
            {
                Name = "propose_code_fix",
                Description = "Proposes a code fix, creates a Git branch, commits the change, and returns branch name.",
                WhenToUse =
                [
                    "A code fix has been identified for an incident.",
                    "Agent needs to prepare a code change for review."
                ],
                WhenNotToUse =
                [
                    "No incident is active.",
                    "User has not approved a code change approach."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["conversation_id"] = new() { Type = "string", Required = true, Description = "Conversation ID.", Example = "a3f5c7e1-9d2b-4a0f-8df3-4f1cd64b21ab" },
                    ["fix_description"] = new() { Type = "string", Required = true, Description = "Description of the fix.", Example = "Fix deduplication logic in webhook handler" },
                    ["file_paths"] = new() { Type = "array", Required = true, Description = "Files to modify.", Example = "[\"src/HiveOps.Api/Controllers/WebhookController.cs\"]" }
                },
                AllowedStates = [ConversationState.InIncidentResolution]
            },
            new AgentToolDefinition
            {
                Name = "propose_db_fix",
                Description = "Proposes a safe database fix script for review and stores it as an attachment.",
                WhenToUse =
                [
                    "A database-level fix is required for an incident.",
                    "Script must be reviewed by user before execution."
                ],
                WhenNotToUse =
                [
                    "Script is unsafe (DELETE/UPDATE without WHERE, DROP, TRUNCATE)."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["conversation_id"] = new() { Type = "string", Required = true, Description = "Conversation ID.", Example = "a3f5c7e1-9d2b-4a0f-8df3-4f1cd64b21ab" },
                    ["sql_script"] = new() { Type = "string", Required = true, Description = "Safe SQL fix script.", Example = "UPDATE Conversations SET Status = 'Active' WHERE Id = '...'" }
                },
                AllowedStates = [ConversationState.InIncidentResolution]
            },
            new AgentToolDefinition
            {
                Name = "deploy_fix",
                Description = "Deploys an approved fix after explicit user confirmation and pipeline health check.",
                WhenToUse =
                [
                    "User explicitly approved a fix and asked to deploy it."
                ],
                WhenNotToUse =
                [
                    "No explicit approval text was given.",
                    "Pipeline is unhealthy."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["conversation_id"] = new() { Type = "string", Required = true, Description = "Conversation ID.", Example = "a3f5c7e1-9d2b-4a0f-8df3-4f1cd64b21ab" },
                    ["approval_text"] = new() { Type = "string", Required = true, Description = "User approval containing 'confirmo' or 'approve'.", Example = "confirmo deploy" }
                },
                AllowedStates = [ConversationState.AwaitingIncidentApproval]
            },
            new AgentToolDefinition
            {
                Name = "read_own_source",
                Description = "Reads source code from the HiveOps repository for self-diagnosis.",
                WhenToUse =
                [
                    "Agent needs to inspect its own codebase to diagnose an incident."
                ],
                WhenNotToUse =
                [
                    "User is asking for commercial/product info."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["file_path"] = new() { Type = "string", Required = true, Description = "Relative file path in the repo.", Example = "src/HiveOps.Agents/Orchestration/IncomingMessageHandler.cs" }
                },
                AllowedStates = [ConversationState.InIncidentAnalysis, ConversationState.InIncidentResolution]
            },
            new AgentToolDefinition
            {
                Name = "run_unit_tests",
                Description = "Runs unit tests for a project and returns pass/fail.",
                WhenToUse =
                [
                    "Before or after proposing a code fix to verify nothing is broken."
                ],
                WhenNotToUse =
                [
                    "No incident is being investigated.",
                    "User is not in a support flow."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["project_path"] = new() { Type = "string", Required = true, Description = "Test project path.", Example = "tests/HiveOps.UnitTests" }
                },
                AllowedStates = [ConversationState.InIncidentAnalysis, ConversationState.InIncidentResolution, ConversationState.AwaitingIncidentApproval]
            },
            new AgentToolDefinition
            {
                Name = "escalate_to_engineer",
                Description = "Escalates the incident to a human engineer.",
                WhenToUse =
                [
                    "Incident is Critical or the bot cannot resolve it.",
                    "User explicitly asks for a human engineer."
                ],
                WhenNotToUse =
                [
                    "A valid support tool can still solve the user intent safely."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["conversation_id"] = new() { Type = "string", Required = true, Description = "Conversation ID.", Example = "a3f5c7e1-9d2b-4a0f-8df3-4f1cd64b21ab" },
                    ["reason"] = new() { Type = "string", Required = true, Description = "Reason for escalation.", Example = "Critical database corruption reported" }
                },
                AllowedStates = [ConversationState.Idle, ConversationState.InIncidentAnalysis, ConversationState.InIncidentResolution, ConversationState.AwaitingIncidentApproval]
            }
        ];
    }
}
