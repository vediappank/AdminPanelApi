using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using RenewalTracker.Platform.Infrastructure.Auth;
using RenewalTracker.Platform.Infrastructure.DependencyInjection;
using RenewalTracker.PlatformApi.Controllers;
using RenewalTracker.PlatformApi.Hubs;
using RenewalTracker.PlatformApi.Json;
using RenewalTracker.PlatformApi.Middleware;
using RenewalTracker.PlatformApi.Services;

try
{
    var builder = WebApplication.CreateBuilder(args);

    ConfigureLogging(builder);
    ConfigureCoreServices(builder);
    ConfigureCors(builder);
    ConfigureRateLimiting(builder);
    ConfigureApplicationServices(builder);

    var app = builder.Build();

    ApplyDatabaseChanges(app);
    LogStartupConfiguration(app);
    LogNotificationConfiguration(app);
    RegisterProcessDiagnostics(app);
    StartBackgroundDiagnostics(app);

    ConfigureMiddleware(app);
    MapEndpoints(app);

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    WriteFatalStartupError(ex);
    Console.Error.WriteLine(ex);
    throw;
}

// -----------------------------------------------------------------------------
// Service registration
// -----------------------------------------------------------------------------

static void ConfigureLogging(WebApplicationBuilder builder)
{
    // Anchor log paths to the application directory. IIS/Plesk does not guarantee
    // that the process working directory is the published application directory.
    log4net.GlobalContext.Properties["LogsDir"] = ApiDiagnostics.LogsFolder;
    Directory.CreateDirectory(ApiDiagnostics.LogsFolder);

    if (!builder.Environment.IsDevelopment())
    {
        // Production logs are written to the configured log4net files rather than
        // an unbounded IIS stdout log.
        builder.Logging.ClearProviders();
    }

    builder.Logging.AddLog4Net(Path.Combine(AppContext.BaseDirectory, "log4net.config"));
}

static void ConfigureCoreServices(WebApplicationBuilder builder)
{
    var configuration = builder.Configuration;
    var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
                     ?? new JwtOptions();

    builder.Services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));

    if (string.IsNullOrWhiteSpace(jwtOptions.Key) ||
        Encoding.UTF8.GetByteCount(jwtOptions.Key) < 32)
    {
        throw new InvalidOperationException(
            $"Jwt:Key is missing or shorter than 32 bytes (environment '{builder.Environment.EnvironmentName}').");
    }

    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")))
    {
        throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    }

    builder.Services.AddPlatformInfrastructure(configuration);

    // This bearer scheme validates platform-operator tokens only. Tenant-user
    // tokens are issued and validated by RenewalTracker.Api with a different key.
    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtOptions.Issuer,
                ValidateAudience = true,
                ValidAudience = jwtOptions.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            };

            // SignalR clients send the JWT in the query string because browser
            // WebSocket APIs cannot set an Authorization header. Accept it only
            // for the Web Leads hub path, never for ordinary API endpoints.
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(accessToken) &&
                        context.HttpContext.Request.Path.StartsWithSegments(WebLeadsHub.Path))
                    {
                        context.Token = accessToken;
                    }

                    return Task.CompletedTask;
                }
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        // All endpoints require authentication unless explicitly marked
        // [AllowAnonymous]. Internal service-key endpoints authenticate using
        // their own service-key requirement.
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.ReferenceHandler =
                System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
            options.JsonSerializerOptions.DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;

            // Accept empty strings for date fields that clients send back but do
            // not edit (for example EnquiryDto.CreatedDate).
            options.JsonSerializerOptions.Converters.Add(new LenientDateTimeConverter());
            options.JsonSerializerOptions.Converters.Add(new LenientNullableDateTimeConverter());
        });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Renewal & Reminder Tracker - Platform API",
            Version = "v1"
        });

        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Enter the JWT access token."
        });

        // Use a reference in the requirement so the security scheme is linked
        // to the definition above without duplicating its metadata.
        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            }] = Array.Empty<string>()
        });

        // Avoid schema-ID collisions when different namespaces contain DTOs
        // with the same class name. This does not hide conflicting API routes.
        options.CustomSchemaIds(type =>
            (type.FullName ?? type.Name).Replace("+", "."));
    });
}

static void ConfigureCors(WebApplicationBuilder builder)
{
    var platformOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                          ?? new[] { "http://localhost:4300" };

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("PlatformAdminClient", policy =>
        {
            policy.WithOrigins(platformOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    });

    // Public website forms are restricted to the configured website origins.
    var websiteOrigins = builder.Configuration.GetSection("Website:AllowedOrigins").Get<string[]>()
                         ?? new[]
                         {
                             "https://blisspointgroup.com",
                             "https://www.blisspointgroup.com"
                         };

    builder.Services.AddCors(options =>
    {
        options.AddPolicy(PublicWebFormsController.WebsiteCorsPolicy, policy =>
        {
            policy.WithOrigins(websiteOrigins)
                .AllowAnyHeader()
                .WithMethods("GET", "POST", "OPTIONS");
        });
    });
}

static void ConfigureRateLimiting(WebApplicationBuilder builder)
{
    var permitLimit = Math.Max(1, builder.Configuration.GetValue("Website:RateLimitPerMinute", 10));

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.ContentType = "application/json";
            await context.HttpContext.Response.WriteAsync(
                "{\"message\":\"Too many submissions from your network. Please wait a minute and try again.\"}",
                cancellationToken);
        };

        options.AddPolicy(PublicWebFormsController.RateLimitPolicy, httpContext =>
        {
            var forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].ToString();
            var clientIp = !string.IsNullOrWhiteSpace(forwardedFor)
                ? forwardedFor.Split(',')[0].Trim()
                : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            return RateLimitPartition.GetFixedWindowLimiter(
                clientIp,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
        });
    });
}

static void ConfigureApplicationServices(WebApplicationBuilder builder)
{
    builder.Services.AddSignalR();

    builder.Services.AddHttpClient(
        WhatsAppCloud.HttpClientName,
        client => client.Timeout = TimeSpan.FromSeconds(30));
    builder.Services.AddSingleton<WhatsAppCloud>();
    builder.Services.AddSingleton<WebLeadNotifier>();

    builder.Services.AddSingleton<PlatformMessagingStore>();
    builder.Services.AddSingleton<PlatformChatbotStore>();
    builder.Services.AddHostedService<PlatformChatbotSeeder>();
    builder.Services.AddHostedService<PlatformMessagingRefresher>();

    builder.Services.AddSingleton<ApiDiagnostics>();

    builder.Services.AddHttpClient(
        DeploymentTracker.HttpClientName,
        client => client.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddSingleton<DeploymentTracker>();
    builder.Services.AddHostedService<DeploymentWatcher>();

    builder.Services.AddSingleton<SchemaMigrator>();
    builder.Services.AddSingleton<WebsiteVisitStore>();
    builder.Services.AddSingleton<SubscriberStore>();
    builder.Services.AddSingleton<SubscriberMessenger>();
    builder.Services.AddSingleton<NewsletterWorker>();
    builder.Services.AddHostedService(serviceProvider =>
        serviceProvider.GetRequiredService<NewsletterWorker>());
}

// -----------------------------------------------------------------------------
// Application initialization and diagnostics
// -----------------------------------------------------------------------------

static void ApplyDatabaseChanges(WebApplication app)
{
    // Keep the existing compatibility change for PlatformUsers.Mobile.
    // Failure is logged but does not prevent the API from starting.
    using (var scope = app.Services.CreateScope())
    {
        try
        {
            var platformDb = scope.ServiceProvider.GetRequiredService<
                RenewalTracker.Platform.Infrastructure.Data.PlatformDbContext>();

            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(
                platformDb.Database,
                "IF OBJECT_ID(N'dbo.PlatformUsers', N'U') IS NOT NULL " +
                "AND COL_LENGTH(N'dbo.PlatformUsers', N'Mobile') IS NULL " +
                "ALTER TABLE dbo.PlatformUsers ADD Mobile NVARCHAR(30) NULL;");
        }
        catch (Exception ex)
        {
            app.Logger.LogError(
                ex,
                "Could not add dbo.PlatformUsers.Mobile. Run DatabaseScripts/PlatformUsers_Mobile.sql on SuperAdminPanel.");
        }
    }

    // Apply packaged migrations in order. SchemaMigrator records failures and
    // reports them in Settings > Deployments.
    app.Services.GetRequiredService<SchemaMigrator>().Run();
}

static void LogStartupConfiguration(WebApplication app)
{
    var serviceKey = app.Configuration["Internal:ServiceKey"] ?? string.Empty;
    var fingerprint = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serviceKey)))[..8];

    app.Logger.LogWarning(
        "STARTUP CONFIG CHECK -> Environment={Environment} | BusinessApiInternal:BaseUrl={BusinessApiBaseUrl} | Internal:ServiceKey fingerprint={ServiceKeyFingerprint}",
        app.Environment.EnvironmentName,
        app.Configuration["BusinessApiInternal:BaseUrl"],
        fingerprint);

    app.Logger.LogInformation(
        "API starting - environment {Environment}, logs folder {LogsFolder}",
        app.Environment.EnvironmentName,
        ApiDiagnostics.LogsFolder);
}

static void LogNotificationConfiguration(WebApplication app)
{
    var smtp = SmtpMail.Read(app.Configuration);
    var whatsapp = app.Services.GetRequiredService<WhatsAppCloud>().Read();
    var acknowledgement = bool.TryParse(
        app.Configuration["Website:SendAcknowledgement"], out var enabled) && !enabled
        ? "off"
        : "on";

    app.Logger.LogInformation(
        "NOTIFICATIONS -> Email: {Email} (host {Host}:{Port}, from {From}, leads to {NotifyEmail}, visitor acknowledgement {Ack}) | WhatsApp: {WhatsApp} (staff numbers {StaffCount}, staff template '{StaffTemplate}', customer template '{CustomerTemplate}')",
        smtp.IsConfigured ? "ON" : "OFF - set Smtp:Host/UserName/Password",
        smtp.Host,
        smtp.Port,
        smtp.FromEmail,
        string.IsNullOrWhiteSpace(app.Configuration["Website:NotifyEmail"])
            ? "(per-form NotifyEmail only)"
            : app.Configuration["Website:NotifyEmail"],
        acknowledgement,
        whatsapp.IsConfigured
            ? "ON"
            : whatsapp.Enabled
                ? "OFF - set WhatsApp:PhoneNumberId/AccessToken"
                : "OFF",
        whatsapp.StaffNumbers.Count,
        whatsapp.StaffTemplate.Name,
        whatsapp.CustomerTemplate.Name);
}

static void RegisterProcessDiagnostics(WebApplication app)
{
    AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        app.Logger.LogCritical(
            eventArgs.ExceptionObject as Exception,
            "Unhandled exception - the API process is stopping");

    TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
    {
        app.Logger.LogError(eventArgs.Exception, "Unobserved background task exception");
        eventArgs.SetObserved();
    };

    app.Lifetime.ApplicationStopping.Register(
        () => app.Logger.LogInformation("API stopping"));
}

static void StartBackgroundDiagnostics(WebApplication app)
{
    _ = Task.Run(async () =>
    {
        try
        {
            var report = await app.Services.GetRequiredService<ApiDiagnostics>().RunAsync();

            if (report.Healthy)
            {
                app.Logger.LogInformation(
                    "STARTUP CHECK OK -> database {Server}/{Database} reachable, Website Leads objects present ({Forms} active forms), website origins: {Origins}",
                    report.DatabaseServer,
                    report.DatabaseName,
                    report.ActiveWebsiteForms,
                    string.Join(", ", report.WebsiteAllowedOrigins));
            }
            else
            {
                foreach (var problem in report.Problems)
                {
                    app.Logger.LogWarning("STARTUP CHECK PROBLEM -> {Problem}", problem);
                }
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Startup check could not run");
        }
    });
}

// -----------------------------------------------------------------------------
// HTTP pipeline and endpoints
// -----------------------------------------------------------------------------

static void ConfigureMiddleware(WebApplication app)
{
    // These must be registered before Swagger so they wrap /swagger/v1/swagger.json
    // and record the underlying exception instead of returning an unexplained 500.
    app.UseMiddleware<RequestResponseLoggingMiddleware>();
    app.UseMiddleware<ExceptionHandlingMiddleware>();

    if (!app.Environment.IsDevelopment())
    {
        app.UseHttpsRedirection();
    }

    if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Swagger:Enabled", false))
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("v1/swagger.json", "RenewalTracker.PlatformApi v1");
            options.RoutePrefix = "swagger";
        });
    }

    // CORS must run before authentication/authorization. PublicWebFormsController
    // selects its own named policy through endpoint metadata.
    app.UseCors("PlatformAdminClient");
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();
}

static void MapEndpoints(WebApplication app)
{
    app.MapControllers();
    app.MapHub<WebLeadsHub>(WebLeadsHub.Path);

    var swaggerEnabled = app.Environment.IsDevelopment() ||
                         app.Configuration.GetValue("Swagger:Enabled", false);

    app.MapGet("/", (IWebHostEnvironment environment) => swaggerEnabled
            ? Results.Redirect("/swagger")
            : Results.Ok(new
            {
                service = "RenewalTracker.PlatformApi",
                status = "ok",
                environment = environment.EnvironmentName
            }))
        .AllowAnonymous()
        .ExcludeFromDescription();

    app.MapGet("/health", async (IWebHostEnvironment environment, bool? check) =>
            check == true
                ? Results.Ok(new
                {
                    status = "ok",
                    environment = environment.EnvironmentName,
                    time = DateTime.Now,
                    checks = await app.Services.GetRequiredService<ApiDiagnostics>().QuickCheckAsync()
                })
                : Results.Ok(new
                {
                    status = "ok",
                    environment = environment.EnvironmentName,
                    time = DateTime.Now
                }))
        .AllowAnonymous();

    app.MapGet("/version", (IWebHostEnvironment environment, SchemaMigrator migrator) =>
    {
        var stamp = DeploymentTracker.ReadOwnStamp(DeploymentTracker.SelfKey, environment);
        var migrationStatus = migrator.Last;
        var applied = new System.Text.Json.Nodes.JsonArray();
        var changed = new System.Text.Json.Nodes.JsonArray();

        foreach (var script in migrationStatus.Scripts)
        {
            if (script.Status is "applied" or "changed")
            {
                applied.Add(script.Name);
            }

            if (script.Status == "changed")
            {
                changed.Add(script.Name);
            }
        }

        stamp["migrations"] = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = migrationStatus.Enabled,
            ["ranAt"] = migrationStatus.RanAt,
            ["applied"] = applied,
            ["changed"] = changed,
            ["failed"] = migrationStatus.Scripts
                .FirstOrDefault(script => script.Status == "failed")?.Name,
            ["pending"] = migrationStatus.Pending
        };

        return Results.Json(stamp);
    })
        .AllowAnonymous()
        .ExcludeFromDescription();
}

static void WriteFatalStartupError(Exception exception)
{
    try
    {
        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "startup-error.txt"),
            $"{DateTime.Now:O}{Environment.NewLine}{exception}");

        var logsDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(logsDirectory);
        File.AppendAllText(
            Path.Combine(logsDirectory, "errors.log"),
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} FATAL Startup - the API could not start: {exception}{Environment.NewLine}");
    }
    catch
    {
        // If the application directory is not writable, the hosting stdout log
        // remains the fallback location for startup exceptions.
    }
}
