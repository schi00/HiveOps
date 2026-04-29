using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Sandpit.Ui.Infrastructure;
using HiveOps.Sandpit.Ui.Middleware;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();

// Sandpit fake tenant context (no real tenant resolution)
builder.Services.AddSingleton<ITenantContext, FakeTenantContext>();

// Configure isolated Sandpit database
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("SandpitDatabase");
    options.UseSqlServer(connectionString, sql =>
    {
        sql.EnableRetryOnFailure(3);
        sql.CommandTimeout(30);
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseMiddleware<SandpitApiKeyMiddleware>();
app.UseMiddleware<SandpitTelemetryMiddleware>();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
