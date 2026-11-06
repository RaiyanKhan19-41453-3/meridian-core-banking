using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using CoreBanking.Api.Data;
using CoreBanking.Api.Middleware;
using CoreBanking.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// --- Database ---
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql =>
        npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
    .UseSnakeCaseNamingConvention());

// --- Auth (JWT bearer) ---
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is not configured.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "core-banking";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "core-banking-clients";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<LedgerService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<GlService>();
builder.Services.AddScoped<LoanService>();
builder.Services.AddScoped<OverdraftService>();
builder.Services.AddScoped<BatchService>();
builder.Services.AddScoped<AmlService>();

// --- ProblemDetails for every unhandled exception ---
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// --- Rate limiting (per user when authenticated, else per IP) ---
// Limits are configuration-driven: code defaults stay strict; dev compose raises them for test automation.
var authLimit = int.TryParse(builder.Configuration["RateLimiting:AuthPermitLimit"], out var a) ? a : 10;
var moneyLimit = int.TryParse(builder.Configuration["RateLimiting:MoneyPermitLimit"], out var mtr) ? mtr : 30;
var windowMinutes = int.TryParse(builder.Configuration["RateLimiting:WindowMinutes"], out var w) ? w : 1;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static string Partition(HttpContext ctx) =>
        ctx.User.Identity?.IsAuthenticated == true
            ? "u:" + (ctx.User.FindFirst("sub")?.Value ?? "unknown")
            : "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(Partition(httpContext), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authLimit,
            Window = TimeSpan.FromMinutes(windowMinutes),
            QueueLimit = 0
        }));

    options.AddPolicy("money", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(Partition(httpContext), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = moneyLimit,
            Window = TimeSpan.FromMinutes(windowMinutes),
            QueueLimit = 0
        }));

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            code = "rate_limited",
            detail = "Too many requests. Slow down and retry.",
            traceId = context.HttpContext.TraceIdentifier
        }, ct);
    };
});

// --- CORS (frontend origins, configurable) ---
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? new[] { "http://localhost:5173", "http://localhost:8091" };
builder.Services.AddCors(options =>
    options.AddPolicy("frontend", policy =>
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

// --- API / Swagger ---
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Core Banking API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the JWT access token: Bearer {token}"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// --- Apply migrations; seed demo data (Development only) ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    if (app.Environment.IsDevelopment() ||
        string.Equals(builder.Configuration["Seed:IncludeDemoData"], "true", StringComparison.OrdinalIgnoreCase))
    {
        await DbSeeder.SeedAsync(db);
    }

    // Reconcile stale batch runs — any unfinished job from a previous process
    // (crash, restart, OOM kill) is marked Failed instead of lingering as Partial.
    var stale = await db.BatchRuns
        .Where(r => r.FinishedAt == null && r.Status == CoreBanking.Api.Entities.BatchRunStatus.Partial)
        .ToListAsync();
    foreach (var r in stale)
    {
        r.Status = CoreBanking.Api.Entities.BatchRunStatus.Failed;
        r.FinishedAt = DateTime.UtcNow;
        r.Errors = JsonSerializer.Serialize(new[] { new { context = "batch.reconcile", error = "interrupted (process exited before finalization)" } });
    }
    if (stale.Count > 0)
    {
        await db.SaveChangesAsync();
        app.Logger.LogWarning("Reconciled {Count} interrupted batch run(s) to Failed", stale.Count);
    }
}

app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Lightweight health endpoint (used by Docker healthcheck)
app.MapGet("/health", async (AppDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    return canConnect
        ? Results.Ok(new { status = "healthy", database = "up" })
        : Results.Problem("database unreachable", statusCode: 503);
});

app.MapControllers();

app.Run();
