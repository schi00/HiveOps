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
                            ["reason"] = "El usuario pidió hablar con un humano"
                        }
                    }
                ],
                AllowedStates = [ConversationState.Idle, ConversationState.InSupport, ConversationState.AwaitingHuman]
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
                    "User asks a functional/how-to question (use search_kb).",
                    "User asks for the status of an existing ticket (use get_incident_status)."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["conversation_id"] = new() { Type = "string", Required = true, Description = "Conversation ID.", Example = "current_conversation_id" },
                    ["description"] = new() { Type = "string", Required = true, Description = "User's incident description.", Example = "Se duplican los mensajes al sincronizar" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Hay un bug, no me llegan las notificaciones",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["conversation_id"] = "current_conversation_id",
                            ["description"] = "No me llegan las notificaciones"
                        }
                    }
                ],
                AllowedStates = [ConversationState.Idle, ConversationState.InSupport, ConversationState.InIncidentAnalysis]
            },
            new AgentToolDefinition
            {
                Name = "provide_incident_info",
                Description = "Appends follow-up details the user provided to the incident that is waiting for information, then re-runs triage.",
                WhenToUse =
                [
                    "User replies with extra details while an incident is pending information.",
                    "User answers a clarification question about their incident."
                ],
                WhenNotToUse =
                [
                    "User reports a brand-new problem (use analyze_incident).",
                    "No incident is pending information."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["conversation_id"] = new() { Type = "string", Required = true, Description = "Conversation ID.", Example = "current_conversation_id" },
                    ["info"] = new() { Type = "string", Required = true, Description = "Additional details provided by the user.", Example = "El error pasa cuando entro a la pantalla de turnos" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Me pasa cuando entro a la pantalla de turnos",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["conversation_id"] = "current_conversation_id",
                            ["info"] = "El error ocurre al entrar a la pantalla de turnos"
                        }
                    }
                ],
                AllowedStates = [ConversationState.InIncidentAnalysis]
            },
            new AgentToolDefinition
            {
                Name = "get_incident_status",
                Description = "Lists the open or recent incidents of the conversation with their status and latest progress.",
                WhenToUse =
                [
                    "User asks for the status of a ticket or incident.",
                    "User asks how their reported problem is going."
                ],
                WhenNotToUse =
                [
                    "User reports a new problem (use analyze_incident)."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["conversation_id"] = new() { Type = "string", Required = true, Description = "Conversation ID.", Example = "current_conversation_id" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "¿Cómo va mi ticket?",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["conversation_id"] = "current_conversation_id"
                        }
                    }
                ],
                AllowedStates = [ConversationState.Idle, ConversationState.InSupport, ConversationState.InIncidentAnalysis]
            },
            new AgentToolDefinition
            {
                Name = "run_db_diagnostic",
                Description = "Runs a safe read-only SELECT query against the tenant database for diagnostics.",
                WhenToUse =
                [
                    "Diagnosing a database incident requires checking real data.",
                    "The triage engine suggested a read-only diagnostic query."
                ],
                WhenNotToUse =
                [
                    "The query modifies data (INSERT/UPDATE/DELETE/DDL are forbidden).",
                    "No incident is under analysis."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["conversation_id"] = new() { Type = "string", Required = true, Description = "Conversation ID.", Example = "current_conversation_id" },
                    ["sql_query"] = new() { Type = "string", Required = true, Description = "Read-only SELECT query.", Example = "SELECT * FROM shifts ORDER BY created_at DESC LIMIT 10" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Revisá los últimos turnos cargados",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["conversation_id"] = "current_conversation_id",
                            ["sql_query"] = "SELECT * FROM shifts ORDER BY created_at DESC LIMIT 10"
                        }
                    }
                ],
                AllowedStates = [ConversationState.InIncidentAnalysis]
            },
            new AgentToolDefinition
            {
                Name = "approve_fix",
                Description = "Deploys the proposed fix after explicit user approval.",
                WhenToUse =
                [
                    "User explicitly approves the proposed fix (e.g. 'confirmo', 'approve')."
                ],
                WhenNotToUse =
                [
                    "User is still describing the problem.",
                    "No fix proposal is pending approval."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["conversation_id"] = new() { Type = "string", Required = true, Description = "Conversation ID.", Example = "current_conversation_id" },
                    ["approval_text"] = new() { Type = "string", Required = true, Description = "User approval text.", Example = "confirmo el deploy del fix" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Confirmo, deployá el fix",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["conversation_id"] = "current_conversation_id",
                            ["approval_text"] = "confirmo el deploy del fix"
                        }
                    }
                ],
                AllowedStates = [ConversationState.AwaitingIncidentApproval, ConversationState.InIncidentAnalysis, ConversationState.InSupport, ConversationState.Idle]
            },
            new AgentToolDefinition
            {
                Name = "reject_fix",
                Description = "Rejects the proposed fix and reopens the incident for review.",
                WhenToUse =
                [
                    "User rejects or cancels the proposed fix."
                ],
                WhenNotToUse =
                [
                    "User approves the fix (use approve_fix).",
                    "No fix proposal is pending approval."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["conversation_id"] = new() { Type = "string", Required = true, Description = "Conversation ID.", Example = "current_conversation_id" },
                    ["reason"] = new() { Type = "string", Required = false, Description = "Reason for rejection.", Example = "prefiero que lo revise un humano" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Rechazo el fix, no me convence",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["conversation_id"] = "current_conversation_id",
                            ["reason"] = "el usuario no confía en el fix propuesto"
                        }
                    }
                ],
                AllowedStates = [ConversationState.AwaitingIncidentApproval, ConversationState.InIncidentAnalysis, ConversationState.InSupport, ConversationState.Idle]
            },
            new AgentToolDefinition
            {
                Name = "search_kb",
                Description = "Searches the tenant knowledge base for articles relevant to a functional or how-to question.",
                WhenToUse =
                [
                    "User asks how to use a feature or a functional question.",
                    "User asks about known issues or documented workarounds."
                ],
                WhenNotToUse =
                [
                    "User reports a new incident (use analyze_incident).",
                    "User asks for ticket status (use get_incident_status)."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["query"] = new() { Type = "string", Required = true, Description = "Search query.", Example = "cómo reasignar un turno" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "¿Cómo reasigno un turno a otro operador?",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["query"] = "cómo reasignar un turno"
                        }
                    }
                ],
                AllowedStates = [ConversationState.Idle, ConversationState.InSupport]
            }
        ];
    }
}
