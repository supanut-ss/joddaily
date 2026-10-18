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

// The frontend (the LIFF page, web/) is a static site of its own, so the browser calls this API cross-origin: allow that site only.
var webOrigins = WebOrigins.Parse(builder.Configuration["App:WebBaseUrl"]);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (webOrigins.Length > 0) p.WithOrigins(webOrigins);
    p.WithHeaders("Authorization", "Content-Type").WithMethods("GET", "POST").SetPreflightMaxAge(TimeSpan.FromHours(1));
}));

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

// Only needed when the app is hosted in a sub-folder of a site (App:PathBase, e.g. "/linejod"); empty on a site of its own.
var pathBase = (builder.Configuration["App:PathBase"] ?? "").Trim().Trim('/');
if (pathBase.Length > 0) app.UsePathBase("/" + pathBase);
app.UseRouting();   // explicit, after UsePathBase: the implicit one runs first and would match endpoints on the unstripped path

app.UseCors();
app.MapControllers();

app.MapGet("/", () => Results.Json(new { app = "LineJod API", ok = true }));   // a status check; the LIFF page is a separate site

app.MapGet("/health", async (AppDbContext db) =>
    await db.Database.CanConnectAsync()
        ? Results.Json(new { ok = true })
        : Results.Json(new { ok = false }, statusCode: 503));

app.Run();

public partial class Program;
