using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using SaaSBot.Domain.Interfaces;
using SaaSBot.Domain.Enums;
using SaaSBot.Infrastructure.Persistence;

namespace SaaSBot.IntegrationTests;

public sealed class WebhookFlowTests : IClassFixture<DashboardWebApplicationFactory>
{
    private readonly DashboardWebApplicationFactory _factory;

    public WebhookFlowTests(DashboardWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task WhatsAppWebhook_Should_Accept_MetaPayload_And_Return_A_Reply()
    {
        using var client = _factory.CreateClient();

        var payload = new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    changes = new[]
                    {
                        new
                        {
                            value = new
                            {
                                messaging_product = "whatsapp",
                                metadata = new
                                {
                                    display_phone_number = "+5491100000001",
                                    phone_number_id = "PHONE_NUMBER_ID_ALPHA"
                                },
                                messages = new[]
                                {
                                    new
                                    {
                                        from = "5491155512345",
                                        id = "wamid.TEST_MESSAGE",
                                        timestamp = "1712875200",
                                        type = "text",
                                        text = new { body = "Hola" }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var response = await client.PostAsJsonAsync("/webhooks/whatsapp", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<WebhookReply>();
        json.Should().NotBeNull();
        json!.Reply.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task WhatsAppWebhook_Should_Greet_Only_Once_Per_Session_And_Keep_Spanish()
    {
        using var client = _factory.CreateClient();

        var firstResponse = await client.PostAsJsonAsync("/webhooks/whatsapp", BuildPayload("wamid.GREETING.1", "5491155512999", "hola"));
        var secondResponse = await client.PostAsJsonAsync("/webhooks/whatsapp", BuildPayload("wamid.GREETING.2", "5491155512999", "hola de nuevo"));

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstJson = await firstResponse.Content.ReadFromJsonAsync<WebhookReply>();
        var secondJson = await secondResponse.Content.ReadFromJsonAsync<WebhookReply>();

        firstJson.Should().NotBeNull();
        secondJson.Should().NotBeNull();
        firstJson!.Reply.Should().Be("Hola, soy Vadi.");
        secondJson!.Reply.Should().StartWith("¿En qué más puedo ayudarte?");
    }

    [Fact]
    public async Task WhatsAppWebhook_Should_Keep_Menu_Button_Available_On_Standard_Replies()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/webhooks/whatsapp", BuildPayload("wamid.MENU.ALWAYS.1", "5491155517030", "hola"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<WebhookReply>();
        json.Should().NotBeNull();
        json!.Reply.Should().Be("Hola, soy Vadi.");

        using var scope = _factory.Services.CreateScope();
        var channel = scope.ServiceProvider.GetRequiredService<IMessagingChannel>();
        var fakeChannel = channel.Should().BeOfType<FakeMessagingChannel>().Subject;
        var menuPayload = fakeChannel.SentButtonMessages.Last(m => m.UserId == "5491155517030");
        menuPayload.Buttons.Should().ContainSingle(b => b.Id == "btn_open_menu" && b.Title == "Menu");
    }

    [Fact]
    public async Task WhatsAppWebhook_Should_Read_Business_Data_And_Reply_In_Spanish()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/webhooks/whatsapp", BuildPayload("wamid.INFO.1", "5491155513888", "cuales son los horarios"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<WebhookReply>();

        json.Should().NotBeNull();
        json!.Reply.Should().Contain("Horarios");
        json.Reply.Should().Contain("Lunes a viernes de 9 a 18 hs.");
        json.Reply.Should().Contain("Envíos");
    }

    [Fact]
    public async Task WhatsAppWebhook_Should_List_Products_For_Brand_Query()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/webhooks/whatsapp", BuildPayload("wamid.BRAND.1", "5491155517444", "mostrame nike"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<WebhookReply>();

        json.Should().NotBeNull();
        json!.Reply.Should().Contain("Nike");
        json.Reply.Should().Contain("Runner Pro");
        json.Reply.Should().NotContain("No entendí");
    }

    [Fact]
    public async Task WhatsAppWebhook_Should_Deduplicate_Repeated_MessageId_And_Send_One_Assistant_Message()
    {
        using var client = _factory.CreateClient();

        var payload = BuildPayload("wamid.DEDUP.1", "5491155514777", "hola");

        var firstResponse = await client.PostAsJsonAsync("/webhooks/whatsapp", payload);
        var secondResponse = await client.PostAsJsonAsync("/webhooks/whatsapp", payload);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstJson = await firstResponse.Content.ReadFromJsonAsync<WebhookReply>();
        var secondJson = await secondResponse.Content.ReadFromJsonAsync<WebhookReply>();

        firstJson!.Reply.Should().Be(secondJson!.Reply);

        using var scope = _factory.Services.CreateScope();
        var channel = scope.ServiceProvider.GetRequiredService<IMessagingChannel>();
        var fakeChannel = channel.Should().BeOfType<FakeMessagingChannel>().Subject;
        fakeChannel.SentMessages.Count(m => m.UserId == "5491155514777").Should().Be(2);
    }

    [Fact]
    public async Task WhatsAppWebhook_Should_Present_Itself_When_Text_Is_Missing()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/webhooks/whatsapp", BuildPayload("wamid.EMPTY.1", "5491155515888", string.Empty));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<WebhookReply>();

        json.Should().NotBeNull();
        json!.Reply.Should().Be("Hola, soy Vadi.");
    }

    [Fact]
    public async Task WhatsAppWebhook_InteractiveButton_ViewCart_Should_Return_Cart_From_Database()
    {
        const string channelUserId = "5491155517020";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.ChannelUserId == channelUserId);
            if (conversation is null)
            {
                conversation = new SaaSBot.Domain.Entities.Conversation
                {
                    TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Channel = "whatsapp",
                    ChannelUserId = channelUserId,
                    Status = ConversationStatus.Active
                };
                db.Conversations.Add(conversation);
                await db.SaveChangesAsync();
            }

            var order = await db.Orders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.ConversationId == conversation.Id && o.Status == OrderStatus.Draft);

            if (order is null)
            {
                order = new SaaSBot.Domain.Entities.Order
                {
                    TenantId = conversation.TenantId,
                    ConversationId = conversation.Id,
                    CustomerPhone = "+549117000",
                    CustomerName = "Alpha Interactive",
                    Status = OrderStatus.Draft,
                    TotalAmount = 0
                };
                db.Orders.Add(order);
                await db.SaveChangesAsync();
            }

            if (order.Items.Count == 0)
            {
                db.OrderItems.AddRange(
                    new SaaSBot.Domain.Entities.OrderItem
                    {
                        TenantId = conversation.TenantId,
                        OrderId = order.Id,
                        ProductId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
                        ProductName = "Runner Pro",
                        Quantity = 1,
                        UnitPrice = 100
                    },
                    new SaaSBot.Domain.Entities.OrderItem
                    {
                        TenantId = conversation.TenantId,
                        OrderId = order.Id,
                        ProductId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"),
                        ProductName = "Dry Fit",
                        Quantity = 1,
                        UnitPrice = 50
                    });
                await db.SaveChangesAsync();
            }
        }

        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/webhooks/whatsapp",
            BuildInteractiveButtonPayload("wamid.BTN.VIEWCART.1", channelUserId, "btn_view_cart", "Ver carrito"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<WebhookReply>();

        json.Should().NotBeNull();
        json!.Reply.Should().Contain("Este es tu carrito");
        json.Reply.Should().Contain("Runner Pro");
        json.Reply.Should().Contain("Dry Fit");
        json.Reply.Should().Contain("Total");
    }

    [Fact]
    public async Task WhatsAppWebhook_InteractiveList_MenuShipping_Should_Return_BusinessConfig_Shipping()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/webhooks/whatsapp",
            BuildInteractiveListPayload("wamid.LIST.SHIP.1", "5491155517000", "menu_shipping", "Calcular envio"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<WebhookReply>();

        json.Should().NotBeNull();
        json!.Reply.Should().NotBeNullOrWhiteSpace();
        json.Reply.Should().Be("¿A qué dirección querés calcular el envío?");
    }

    [Fact]
    public async Task WhatsAppWebhook_Text_Menu_Should_Return_Interactive_List_Menu()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/webhooks/whatsapp", BuildPayload("wamid.MENU.TEXT.1", "5491155517999", "menu"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<WebhookReply>();
        json.Should().NotBeNull();
        json!.Reply.Should().Contain("menú rápido");

        using var scope = _factory.Services.CreateScope();
        var channel = scope.ServiceProvider.GetRequiredService<IMessagingChannel>();
        var fakeChannel = channel.Should().BeOfType<FakeMessagingChannel>().Subject;
        fakeChannel.SentListMessages.Should().Contain(m => m.UserId == "5491155517999");
    }

    [Fact]
    public async Task WhatsAppWebhook_MenuHelpChoose_Should_Start_Guided_Flow_Without_Searching()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/webhooks/whatsapp",
            BuildInteractiveListPayload("wamid.LIST.HELP.1", "5491155517998", "menu_help_choose", "Ayudame a elegir"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<WebhookReply>();
        json.Should().NotBeNull();
        json!.Reply.Should().Be("Claro. ¿Para qué deporte estás buscando?");
    }

    [Fact]
    public async Task WhatsAppWebhook_MenuPayments_Should_Return_Default_Payment_Methods_When_Not_Configured()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/webhooks/whatsapp",
            BuildInteractiveListPayload("wamid.LIST.PAY.1", "5491155517997", "menu_payments", "Ver medios de pago"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<WebhookReply>();
        json.Should().NotBeNull();
        json!.Reply.Should().Be("Aceptamos efectivo y transferencia");
    }

    [Fact]
    public async Task WhatsAppWebhook_Should_Reuse_Previous_Inventory_Context_For_Lowest_Price_Refinement()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

            db.Products.AddRange(
                new SaaSBot.Domain.Entities.Product
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Name = "Zapatillas Alpha Pro",
                    Brand = "Nike",
                    Category = "Calzado",
                    Description = "Modelo premium",
                    Tags = "zapatillas,nike",
                    Price = 120000,
                    StockQuantity = 10,
                    Sku = "ALPHA-ZAP-1"
                },
                new SaaSBot.Domain.Entities.Product
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Name = "Zapatillas Alpha Lite",
                    Brand = "Adidas",
                    Category = "Calzado",
                    Description = "Modelo accesible",
                    Tags = "zapatillas,adidas",
                    Price = 70000,
                    StockQuantity = 3,
                    Sku = "ALPHA-ZAP-2"
                });

            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();

        var first = await client.PostAsJsonAsync("/webhooks/whatsapp", BuildPayload("wamid.CTX.1", "5491155517996", "quiero zapatillas"));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsJsonAsync("/webhooks/whatsapp", BuildPayload("wamid.CTX.2", "5491155517996", "lo mas barato"));
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await second.Content.ReadFromJsonAsync<WebhookReply>();

        json.Should().NotBeNull();
        json!.Reply.IndexOf("Zapatillas Alpha Lite", StringComparison.Ordinal).Should().BeLessThan(json.Reply.IndexOf("Zapatillas Alpha Pro", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatsAppWebhook_WhenAwaitingHuman_And_UserRejectsHandoff_Should_Return_Fallback()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.ChannelUserId == "5491155517000");
        if (conversation is null)
        {
            conversation = new SaaSBot.Domain.Entities.Conversation
            {
                TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Channel = "whatsapp",
                ChannelUserId = "5491155517000",
                Status = ConversationStatus.Active
            };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        conversation.Status = ConversationStatus.AwaitingHuman;
        await db.SaveChangesAsync();

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/webhooks/whatsapp", BuildPayload("wamid.HANDOFF.REJECT.1", "5491155517000", "no quiero"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<WebhookReply>();
        json.Should().NotBeNull();
        json!.Reply.Should().Be("No entendí del todo tu consulta. ¿Podrías reformularla?");
    }

    [Fact]
    public async Task WhatsAppWebhook_WhenAwaitingHuman_And_UserSaysHola_Should_Return_Fallback()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.ChannelUserId == "5491155517010");
        if (conversation is null)
        {
            conversation = new SaaSBot.Domain.Entities.Conversation
            {
                TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Channel = "whatsapp",
                ChannelUserId = "5491155517010",
                Status = ConversationStatus.Active
            };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        conversation.Status = ConversationStatus.AwaitingHuman;
        await db.SaveChangesAsync();

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/webhooks/whatsapp", BuildPayload("wamid.HANDOFF.HOLA.1", "5491155517010", "hola"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<WebhookReply>();
        json.Should().NotBeNull();
        json!.Reply.Should().Be("No entendí del todo tu consulta. ¿Podrías reformularla?");
    }

    private static object BuildPayload(string messageId, string from, string text)
    {
        return new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    changes = new[]
                    {
                        new
                        {
                            value = new
                            {
                                messaging_product = "whatsapp",
                                metadata = new
                                {
                                    display_phone_number = "+5491100000001",
                                    phone_number_id = "PHONE_NUMBER_ID_ALPHA"
                                },
                                messages = new[]
                                {
                                    new
                                    {
                                        from,
                                        id = messageId,
                                        timestamp = "1712875200",
                                        type = "text",
                                        text = new { body = text }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static object BuildInteractiveButtonPayload(string messageId, string from, string buttonId, string title)
    {
        return new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    changes = new[]
                    {
                        new
                        {
                            value = new
                            {
                                messaging_product = "whatsapp",
                                metadata = new
                                {
                                    display_phone_number = "+5491100000001",
                                    phone_number_id = "PHONE_NUMBER_ID_ALPHA"
                                },
                                messages = new[]
                                {
                                    new
                                    {
                                        from,
                                        id = messageId,
                                        timestamp = "1712875200",
                                        type = "interactive",
                                        interactive = new
                                        {
                                            type = "button_reply",
                                            button_reply = new
                                            {
                                                id = buttonId,
                                                title
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static object BuildInteractiveListPayload(string messageId, string from, string listReplyId, string title)
    {
        return new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    changes = new[]
                    {
                        new
                        {
                            value = new
                            {
                                messaging_product = "whatsapp",
                                metadata = new
                                {
                                    display_phone_number = "+5491100000001",
                                    phone_number_id = "PHONE_NUMBER_ID_ALPHA"
                                },
                                messages = new[]
                                {
                                    new
                                    {
                                        from,
                                        id = messageId,
                                        timestamp = "1712875200",
                                        type = "interactive",
                                        interactive = new
                                        {
                                            type = "list_reply",
                                            list_reply = new
                                            {
                                                id = listReplyId,
                                                title,
                                                description = "menu option"
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private sealed record WebhookReply(string Reply);
}