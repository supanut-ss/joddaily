using Microsoft.EntityFrameworkCore;
using LineJod.Config;
using LineJod.Data;
using LineJod.Line;
using LineJod.Services;

var builder = WebApplication.CreateBuilder(args);

// Optional machine-local overrides (secrets) that must never be committed.
builder.Configuration.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false);

builder.Services.Configure<AppSettings>(builder.Configuration);
builder.Services.AddControllers();

var serverVersion = builder.Configuration["Db:ServerVersion"] ?? "11.8.8-mariadb";
builder.Services.AddDbContext<AppDbContext>(o => o
    .UseMySql(builder.Configuration.GetConnectionString("Default") ?? "", ServerVersion.Parse(serverVersion))
    .UseSnakeCaseNamingConvention());

builder.Services.AddHttpClient<LineClient>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<LiffAuth>();
builder.Services.AddScoped<LedgerService>();
builder.Services.AddScoped<LedgerChatService>();
builder.Services.AddScoped<LedgerNotifier>();
builder.Services.AddScoped<WebhookProcessor>();
builder.Services.AddScoped<RichMenuService>();

var app = builder.Build();

app.UseDefaultFiles();   // "/" serves wwwroot/index.html (the LIFF page)
app.UseStaticFiles();
app.MapControllers();

app.MapGet("/health", async (AppDbContext db) =>
    await db.Database.CanConnectAsync()
        ? Results.Json(new { ok = true })
        : Results.Json(new { ok = false }, statusCode: 503));

app.Run();

public partial class Program;
