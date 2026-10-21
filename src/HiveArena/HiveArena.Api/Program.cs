using HiveArena.Api.Configuration;
using HiveArena.Api.Middleware;
using HiveArena.Api.Scenarios;
using HiveArena.Api.Validation;
using HiveArena.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<HiveArenaHostOptions>(builder.Configuration.GetSection(HiveArenaHostOptions.SectionName));
builder.Services.AddHiveArenaConfiguration(builder.Configuration);
builder.Services.AddHiveArenaInfrastructure();
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
});

builder.Services.AddControllersWithViews();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseMiddleware<HiveArenaApiKeyMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
