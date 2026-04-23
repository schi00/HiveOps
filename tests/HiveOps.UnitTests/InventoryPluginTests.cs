using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Moq;
using HiveOps.Agents.Inventory;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.UnitTests;

public sealed class InventoryPluginTests
{
    [Fact]
    public async Task SearchProductsAsync_Should_Use_Synonyms_And_Concept_Maps_For_Football_Query()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);

        var conceptId = Guid.NewGuid();
        db.Synonyms.Add(new Synonym { Id = Guid.NewGuid(), TenantId = tenantId, Term = "football", Normalized = "futbol" });
        db.Concepts.Add(new Concept { Id = conceptId, TenantId = tenantId, Name = "futbol", Type = "sport" });
        db.ConceptProductMaps.Add(new ConceptProductMap
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConceptId = conceptId,
            Category = "Futbol",
            Tags = "botines,pelota",
            Priority = 10
        });

        db.Products.AddRange(
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Botines Adidas Predator League", Brand = "Adidas", Category = "Futbol", Description = "Control optimizado", Tags = "futbol,botines,adidas", Price = 109990, StockQuantity = 18, Sku = "ALFA-AD-004" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Pelota Nike Academy Team", Brand = "Nike", Category = "Futbol", Description = "Uso frecuente", Tags = "pelota,futbol,nike", Price = 29990, StockQuantity = 24, Sku = "ALFA-NK-007" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Pelotas Wilson Championship x3", Brand = "Wilson", Category = "Tenis", Description = "Buen pique", Tags = "tenis,pelotas,wilson", Price = 14990, StockQuantity = 21, Sku = "ALFA-WL-044" });

        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Loose);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Loose);
        embeddingService
            .Setup(s => s.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        searchService
            .Setup(s => s.HybridSearchAsync(
                tenantId,
                It.IsAny<string>(),
                It.IsAny<float[]>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProductSearchResult
                {
                    ProductId = Guid.NewGuid(),
                    Name = "Raqueta Head Spark Elite",
                    Brand = "Head",
                    Description = "Raqueta para principiantes",
                    Tags = "tenis,raqueta,head",
                    Price = 119990,
                    StockQuantity = 7,
                    Sku = "ALFA-HD-045"
                }
            ]);
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var result = await sut.SearchProductsAsync(kernel, "quiero algo para jugar al football");

        result.Should().Contain("Botines Adidas Predator League");
        result.Should().Contain("Pelota Nike Academy Team");
        result.Should().NotContain("Pelotas Wilson Championship x3");
    }

    [Fact]
    public async Task SearchProductsAsync_Should_Guide_For_Too_Generic_Sports_Query()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);
        db.Products.AddRange(
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Auriculares JBL Endurance Run 2", Brand = "JBL", Category = "Tecnologia Deportiva", Description = "Auriculares deportivos", Tags = "auriculares,jbl,deporte", Price = 45990, StockQuantity = 12, Sku = "ALFA-JB-039" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Protector Bucal Shock Doctor", Brand = "Shock Doctor", Category = "Boxeo", Description = "Protector bucal", Tags = "boxeo,protector,bucal", Price = 9990, StockQuantity = 35, Sku = "ALFA-SD-049" });
        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var result = await sut.SearchProductsAsync(kernel, "algo para hacer deporte");

        result.Should().Contain("fútbol");
        result.Should().Contain("tenis");
        result.Should().Contain("running");
        result.Should().Contain("gym");
        result.Should().NotContain("Auriculares JBL Endurance Run 2");
    }

    [Fact]
    public async Task SearchProductsAsync_Should_Recommend_Running_Shoes_For_Start_Running_Query()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);
        db.Products.AddRange(
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Zapatillas Nike Pegasus 41", Brand = "Nike", Category = "Calzado Running", Description = "Livianas para correr", Tags = "nike,running,zapatillas", Price = 134990, StockQuantity = 14, Sku = "ALFA-NK-001" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Remera Adidas Own The Run", Brand = "Adidas", Category = "Ropa Deportiva", Description = "Remera técnica", Tags = "remera,adidas,running", Price = 24990, StockQuantity = 34, Sku = "ALFA-AD-008" });
        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var result = await sut.SearchProductsAsync(kernel, "quiero empezar a correr");

        result.Should().Contain("Calzado Running");
        result.Should().Contain("Zapatillas Nike Pegasus 41");
    }

    [Fact]
    public async Task SearchProductsAsync_Should_Sort_By_Lowest_Price_When_Query_Asks_For_The_Cheapest()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);
        db.Products.AddRange(
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Zapatillas Premium", Brand = "Nike", Category = "Calzado", Description = "Alta gama", Tags = "zapatillas,nike", Price = 120000, StockQuantity = 50, Sku = "SKU-HP" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Zapatillas Entry", Brand = "Adidas", Category = "Calzado", Description = "Acceso", Tags = "zapatillas,adidas", Price = 70000, StockQuantity = 2, Sku = "SKU-LP" });
        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var result = await sut.SearchProductsAsync(kernel, "zapatillas lo mas barato");

        result.IndexOf("Zapatillas Entry", StringComparison.Ordinal).Should().BeLessThan(result.IndexOf("Zapatillas Premium", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchProductsAsync_Should_Return_Honest_Fallback_For_Unsupported_Basket_Query()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);
        db.Synonyms.Add(new Synonym { Id = Guid.NewGuid(), TenantId = tenantId, Term = "basket", Normalized = "basquet" });
        db.Products.Add(new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Auriculares JBL Endurance Run 2", Brand = "JBL", Category = "Tecnologia Deportiva", Description = "Auriculares deportivos", Tags = "auriculares,jbl,deporte", Price = 45990, StockQuantity = 12, Sku = "ALFA-JB-039" });
        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var result = await sut.SearchProductsAsync(kernel, "articulos para basquet");

        result.Should().Contain("básquet");
        result.Should().Contain("No tengo productos específicos");
        result.Should().NotContain("Auriculares JBL Endurance Run 2");
    }

    [Fact]
    public async Task SearchProductsAsync_Should_Return_Coherent_Negative_Response_For_Natacion_Negative_Statement()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);
        db.Products.Add(new Product
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Guantes de Boxeo Everlast 12 oz",
            Brand = "Everlast",
            Category = "Boxeo",
            Description = "Entrenamiento",
            Tags = "boxeo,guantes",
            Price = 49990,
            StockQuantity = 13,
            Sku = "BX-001"
        });
        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var result = await sut.SearchProductsAsync(kernel, "ahi no hay cosas de natacion");

        result.Should().Contain("Por ahora no tenemos artículos de natación");
        result.Should().Contain("alternativas similares");
        result.Should().NotContain("No encontré productos para");
    }

    [Fact]
    public async Task SearchProductsAsync_Should_Not_Return_Generic_Guidance_For_Typo_Query_With_Concrete_Product_Intention()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);
        db.Products.AddRange(
            new Product
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Raqueta Wilson Pro Staff Team",
                Brand = "Wilson",
                Category = "Tenis",
                Description = "Raqueta para jugadores intermedios",
                Tags = "tenis,raqueta,wilson",
                Price = 169990,
                StockQuantity = 6,
                Sku = "ALFA-WL-043"
            },
            new Product
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Raqueta Head Spark Elite",
                Brand = "Head",
                Category = "Tenis",
                Description = "Raqueta para principiantes",
                Tags = "tenis,raqueta,head",
                Price = 119990,
                StockQuantity = 7,
                Sku = "ALFA-HD-045"
            });
        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Loose);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Loose);
        embeddingService
            .Setup(s => s.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        searchService
            .Setup(s => s.HybridSearchAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<float[]>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProductSearchResult>());
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var result = await sut.SearchProductsAsync(kernel, "quiero ver que raquetas de tenia tienen estoy buscando algo barato");

        result.Should().NotContain("Te puedo orientar mejor");
        result.Should().Contain("No encontré productos");
    }

    [Fact]
    public async Task SearchProductsAsync_Should_List_Brands_For_General_Brand_Question()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);
        db.Products.AddRange(
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Zapatillas A", Brand = "Nike", Category = "Calzado", Description = "", Price = 1, StockQuantity = 1, Sku = "SKU-1" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Zapatillas B", Brand = "Adidas", Category = "Calzado", Description = "", Price = 1, StockQuantity = 1, Sku = "SKU-2" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Remera C", Brand = "Nike", Category = "Ropa", Description = "", Price = 1, StockQuantity = 1, Sku = "SKU-3" });
        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var result = await sut.SearchProductsAsync(kernel, "que marcas venden?");

        result.Should().Contain("marcas");
        result.Should().Contain("• Adidas");
        result.Should().Contain("\n• Nike");
        result.Should().Contain("Nike");
        result.Should().Contain("Adidas");
    }

    [Fact]
    public async Task SearchProductsAsync_Should_List_Product_Types_For_Vague_Product_Question()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);
        db.Products.AddRange(
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Prod A", Brand = "Marca1", Category = "Calzado", Description = "", Price = 1, StockQuantity = 1, Sku = "SKU-11" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Prod B", Brand = "Marca2", Category = "Accesorios", Description = "", Price = 1, StockQuantity = 1, Sku = "SKU-12" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Prod C", Brand = "Marca3", Category = "Calzado", Description = "", Price = 1, StockQuantity = 1, Sku = "SKU-13" });
        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var result = await sut.SearchProductsAsync(kernel, "que productos venden?");

        result.Should().Contain("tipos de productos");
        result.Should().Contain("• Accesorios");
        result.Should().Contain("\n• Calzado");
        result.Should().Contain("Calzado");
        result.Should().Contain("Accesorios");
    }

    [Fact]
    public async Task CheckStockAsync_Should_Return_ExactProductStock_ForRequestedProduct()
    {
        var tenantId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);
        db.Products.AddRange(
            new Product
            {
                Id = productId,
                TenantId = tenantId,
                Name = "Producto exacto",
                Brand = "Marca A",
                Category = "Cat",
                Description = "Desc",
                Price = 10,
                StockQuantity = 7,
                Sku = "SKU-1"
            },
            new Product
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Producto similar",
                Brand = "Marca B",
                Category = "Cat",
                Description = "Desc",
                Price = 20,
                StockQuantity = 99,
                Sku = "SKU-2"
            });
        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var raw = await sut.CheckStockAsync(kernel, productId.ToString());

        raw.Should().Contain("7 unidad(es)");
        raw.Should().Contain("disponible");
    }

    [Fact]
    public async Task SearchProductsAsync_Should_List_Gym_Equipment_Instead_Of_Generic_Categories()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);
        db.Products.AddRange(
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Mancuerna Hexagonal 10kg", Brand = "Everlast", Category = "Equipamiento Gym", Description = "Ideal para fuerza", Tags = "gym pesas negro", Price = 45000, StockQuantity = 6, Sku = "GYM-1" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Banda Elastica Pro", Brand = "Domyos", Category = "Equipamiento Gym", Description = "Trabajo funcional", Tags = "gym banda verde", Price = 18000, StockQuantity = 10, Sku = "GYM-2" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Zapatillas Runner", Brand = "Nike", Category = "Calzado", Description = "Running urbano", Tags = "zapatillas negro 42", Price = 120000, StockQuantity = 4, Sku = "RUN-1" });
        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var result = await sut.SearchProductsAsync(kernel, "que equipamiento de gym venden?");

        result.Should().Contain("Equipamiento Gym");
        result.Should().Contain("Mancuerna Hexagonal 10kg");
        result.Should().Contain("Banda Elastica Pro");
        result.Should().NotContain("tipos de productos");
    }

    [Fact]
    public async Task SearchProductsAsync_Should_List_All_Products_For_Brand_Query()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);
        db.Products.AddRange(
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Pegasus 40", Brand = "Nike", Category = "Calzado", Description = "Running", Tags = "negro 44", Price = 150000, StockQuantity = 3, Sku = "NK-1" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Remera Dri-Fit", Brand = "Nike", Category = "Ropa Deportiva", Description = "Training", Tags = "azul m", Price = 55000, StockQuantity = 8, Sku = "NK-2" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Classic Tee", Brand = "Adidas", Category = "Ropa Deportiva", Description = "Casual", Tags = "blanco m", Price = 42000, StockQuantity = 7, Sku = "AD-1" });
        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var result = await sut.SearchProductsAsync(kernel, "mostrame nike");

        result.Should().Contain("productos que encontré de Nike");
        result.Should().Contain("Pegasus 40");
        result.Should().Contain("Remera Dri-Fit");
        result.Should().NotContain("Classic Tee");
    }

    [Fact]
    public async Task SearchProductsAsync_Should_Filter_By_Color_And_Size_From_Tags()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);
        db.Products.AddRange(
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Runner Pro", Brand = "Nike", Category = "Calzado", Description = "Zapatillas urbanas", Tags = "zapatillas negro talle 44", Price = 120000, StockQuantity = 5, Sku = "ZAP-1" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Street Move", Brand = "Puma", Category = "Calzado", Description = "Zapatillas training", Tags = "negra 44 running", Price = 98000, StockQuantity = 2, Sku = "ZAP-2" },
            new Product { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Light Run", Brand = "Adidas", Category = "Calzado", Description = "Zapatillas running", Tags = "blanco talle 42", Price = 110000, StockQuantity = 6, Sku = "ZAP-3" });
        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var result = await sut.SearchProductsAsync(kernel, "zapatillas negras talle 44");

        result.Should().Contain("Runner Pro");
        result.Should().Contain("Street Move");
        result.Should().NotContain("Light Run");
        result.Should().Contain("color negro");
        result.Should().Contain("talle 44");
    }

    [Fact]
    public async Task SearchProductsAsync_Should_Match_Dynamic_Attribute_Values()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options, tenantContext);
        var boot = new Product
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Botín Attack",
            Brand = "Topper",
            Category = "Calzado",
            Description = "Botín de césped sintético",
            Tags = "futbol",
            Price = 89000,
            StockQuantity = 4,
            Sku = "BOT-1"
        };
        var generic = new Product
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Botín Classic",
            Brand = "Topper",
            Category = "Calzado",
            Description = "Modelo base",
            Tags = "futbol",
            Price = 76000,
            StockQuantity = 6,
            Sku = "BOT-2"
        };

        db.Products.AddRange(boot, generic);
        db.ProductAttributeValues.AddRange(
            new ProductAttributeValue
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProductId = boot.Id,
                AttributeKey = "material",
                AttributeValue = "Cuero",
                NormalizedValue = "cuero"
            },
            new ProductAttributeValue
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProductId = generic.Id,
                AttributeKey = "material",
                AttributeValue = "Sintético",
                NormalizedValue = "sintetico"
            });
        await db.SaveChangesAsync();

        var searchService = new Mock<IProductSearchService>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var sut = new InventoryPlugin(searchService.Object, embeddingService.Object, db, NullLogger<InventoryPlugin>.Instance);

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Data[KernelConstants.TenantIdKey] = tenantId;

        var result = await sut.SearchProductsAsync(kernel, "topper cuero");

        result.Should().Contain("Botín Attack");
        result.Should().NotContain("Botín Classic");
    }
}
