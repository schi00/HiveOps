using HiveOps.Application.Models;

namespace HiveOps.Api.Services;

public sealed class IndustrySettingsService
{
    public TenantAdminSettings BuildDefaults(string? industry)
    {
        var key = (industry ?? string.Empty).Trim().ToLowerInvariant();

        return key switch
        {
            "deportes" or "sport" or "running" => BuildSportsDefaults(),
            "moda" or "fashion" or "urbano" => BuildFashionDefaults(),
            "outdoor" or "trekking" => BuildOutdoorDefaults(),
            _ => BuildGenericDefaults()
        };
    }

    public TenantAdminSettings MergeMissingValues(TenantAdminSettings current, TenantAdminSettings defaults)
    {
        current.BotBehavior.AssistantName = string.IsNullOrWhiteSpace(current.BotBehavior.AssistantName) ? defaults.BotBehavior.AssistantName : current.BotBehavior.AssistantName;
        current.BotBehavior.Tone = string.IsNullOrWhiteSpace(current.BotBehavior.Tone) ? defaults.BotBehavior.Tone : current.BotBehavior.Tone;
        current.BotBehavior.ResponseLanguage = string.IsNullOrWhiteSpace(current.BotBehavior.ResponseLanguage) ? defaults.BotBehavior.ResponseLanguage : current.BotBehavior.ResponseLanguage;
        if (current.BotBehavior.MaxResponseTokens <= 0) current.BotBehavior.MaxResponseTokens = defaults.BotBehavior.MaxResponseTokens;

        current.Phrases.WelcomeMessage = string.IsNullOrWhiteSpace(current.Phrases.WelcomeMessage) ? defaults.Phrases.WelcomeMessage : current.Phrases.WelcomeMessage;
        current.Phrases.FallbackMessage = string.IsNullOrWhiteSpace(current.Phrases.FallbackMessage) ? defaults.Phrases.FallbackMessage : current.Phrases.FallbackMessage;
        current.Phrases.HumanHandoffMessage = string.IsNullOrWhiteSpace(current.Phrases.HumanHandoffMessage) ? defaults.Phrases.HumanHandoffMessage : current.Phrases.HumanHandoffMessage;

        current.WhatsApp.PhoneNumber = string.IsNullOrWhiteSpace(current.WhatsApp.PhoneNumber) ? defaults.WhatsApp.PhoneNumber : current.WhatsApp.PhoneNumber;
        current.WhatsApp.Provider = string.IsNullOrWhiteSpace(current.WhatsApp.Provider) ? defaults.WhatsApp.Provider : current.WhatsApp.Provider;

        return current;
    }

    private static TenantAdminSettings BuildSportsDefaults() => new()
    {
        BotBehavior = new BotBehaviorSettings
        {
            AssistantName = "Coach de Tienda",
            Tone = "Energetico y claro",
            ResponseLanguage = "es-AR",
            MaxResponseTokens = 550
        },
        Phrases = new PhraseSettings
        {
            WelcomeMessage = "Hola, te ayudo a elegir equipamiento y talle ideal.",
            FallbackMessage = "No terminé de entender. Decime deporte, marca o talle y te guío.",
            HumanHandoffMessage = "Te conecto con un asesor deportivo para cerrar la compra."
        }
    };

    private static TenantAdminSettings BuildFashionDefaults() => new()
    {
        BotBehavior = new BotBehaviorSettings
        {
            AssistantName = "Stylist Assistant",
            Tone = "Cercano y aspiracional",
            ResponseLanguage = "es-AR",
            MaxResponseTokens = 650
        },
        Phrases = new PhraseSettings
        {
            WelcomeMessage = "Hola, te ayudo con looks, talles y disponibilidad al instante.",
            FallbackMessage = "¿Me das más detalle? por ejemplo prenda, estilo o talle.",
            HumanHandoffMessage = "Te conecto con un asesor de estilo para ayudarte mejor."
        }
    };

    private static TenantAdminSettings BuildOutdoorDefaults() => new()
    {
        BotBehavior = new BotBehaviorSettings
        {
            AssistantName = "Guia Outdoor",
            Tone = "Tecnico y confiable",
            ResponseLanguage = "es-AR",
            MaxResponseTokens = 700
        },
        Phrases = new PhraseSettings
        {
            WelcomeMessage = "Hola, te ayudo a equiparte para trekking y aventura.",
            FallbackMessage = "Contame tu actividad o clima objetivo y te recomiendo mejor.",
            HumanHandoffMessage = "Te paso con un especialista outdoor para una recomendación precisa."
        }
    };

    private static TenantAdminSettings BuildGenericDefaults() => new();
}