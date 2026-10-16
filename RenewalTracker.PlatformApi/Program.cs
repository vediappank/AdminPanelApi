using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

    // ---------------------------------------------------------------------
    // Logging - log4net alongside the default console provider (Console still
    // shows everything while debugging in Visual Studio; log4net additionally
    // writes the durable Logs/platform-api.log and Logs/requests.log files -
    // see log4net.config and RequestResponseLoggingMiddleware).
    // ---------------------------------------------------------------------
    builder.Logging.AddLog4Net("log4net.config");

    // ---------------------------------------------------------------------
    // Configuration
    // ---------------------------------------------------------------------
    builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
    var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

    if (string.IsNullOrWhiteSpace(jwtOptions.Key) || Encoding.UTF8.GetByteCount(jwtOptions.Key) < 32)
        throw new InvalidOperationException(
            $"Jwt:Key is missing or shorter than 32 characters (environment '{builder.Environment.EnvironmentName}').");

    if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("DefaultConnection")))
        throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

    // ---------------------------------------------------------------------
    // EF Core (PlatformDbContext / RenewalTrackerPlatform), platform-operator
    // identity system, tenant-provisioning client
    // ---------------------------------------------------------------------
    builder.Services.AddPlatformInfrastructure(builder.Configuration);

    // This bearer scheme only ever validates platform-operator tokens
    // (PlatformJwtTokenService) - a tenant-user token is issued and validated
    // exclusively by RenewalTracker.Api now, signed with a different Jwt:Key,
    // so it fails signature validation here before RequirePlatformAuth even
    // runs. See PlatformCurrentUserService.IsAuthenticated.
    builder.Services.AddAuthentication(options =>
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
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        // SignalR (Website Leads live alerts): browsers can't set an
        // Authorization header on a WebSocket, so the Angular client sends
        // the same platform JWT as ?access_token=... - accepted ONLY on the
        // hub path, never for ordinary API calls.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments(WebLeadsHub.Path))
                    context.Token = accessToken;
                return Task.CompletedTask;
            },
        };
    });

    builder.Services.AddAuthorization(options =>
    {
        // Every endpoint requires a valid JWT unless explicitly [AllowAnonymous]
        // (login, and the RequireInternalServiceKey-gated internal endpoints,
        // which authenticate on their own header instead).
        options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });

    // ---------------------------------------------------------------------
    // MVC / Swagger / CORS
    // ---------------------------------------------------------------------
    builder.Services.AddControllers().AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;

        // Tolerate "" for date fields the client sends back but never actually
        // edits (e.g. EnquiryDto.CreatedDate) - see Json/LenientDateTimeConverters.cs
        // for why the strict default converter turns that into a 400 on the
        // whole request instead of just that one field.
        options.JsonSerializerOptions.Converters.Add(new LenientDateTimeConverter());
        options.JsonSerializerOptions.Converters.Add(new LenientNullableDateTimeConverter());
    });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "Renewal & Reminder Tracker - Platform API", Version = "v1" });
        var jwtScheme = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Paste only the raw JWT (no 'Bearer ' prefix needed here).",
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
        };
        c.AddSecurityDefinition("Bearer", jwtScheme);
        c.AddSecurityRequirement(new OpenApiSecurityRequirement { { jwtScheme, Array.Empty<string>() } });
    });

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

    // Website Leads: blisspointgroup.com forms post (anonymously) to
    // api/public/web-forms/* - see PublicWebFormsController. Only the
    // website's own origins may call it from a browser.
    var websiteOrigins = builder.Configuration.GetSection("Website:AllowedOrigins").Get<string[]>()
        ?? new[] { "https://blisspointgroup.com", "https://www.blisspointgroup.com" };

    builder.Services.AddCors(options =>
    {
        options.AddPolicy(PublicWebFormsController.WebsiteCorsPolicy, policy =>
        {
            policy.WithOrigins(websiteOrigins)
                  .AllowAnyHeader()
                  .WithMethods("GET", "POST", "OPTIONS");   // GET: api/public/web-settings
        });
    });

    // Website Leads: at most N submissions per visitor IP per minute
    // (Website:RateLimitPerMinute, default 10) - stops form-flooding bots.
    var perMinute = builder.Configuration.GetValue("Website:RateLimitPerMinute", 10);
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.ContentType = "application/json";
            await context.HttpContext.Response.WriteAsync(
                "{\"message\":\"Too many submissions from your network. Please wait a minute and try again.\"}", ct);
        };
        options.AddPolicy(PublicWebFormsController.RateLimitPolicy, httpContext =>
        {
            var forwarded = httpContext.Request.Headers["X-Forwarded-For"].ToString();
            var ip = !string.IsNullOrWhiteSpace(forwarded)
                ? forwarded.Split(',')[0].Trim()
                : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, perMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        });
    });

    // Website Leads: real-time alerts to the Admin Panel (/hubs/web-leads).
    builder.Services.AddSignalR();
    // WhatsApp Business Platform (Cloud API) - new-lead alerts + visitor confirmations ("WhatsApp" section).
    builder.Services.AddHttpClient(WhatsAppCloud.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
    builder.Services.AddSingleton<WhatsAppCloud>();
    builder.Services.AddSingleton<WebLeadNotifier>();
    // Self-check (database objects, Logs folder, origins) - startup log line + GET api/platform/diagnostics
    builder.Services.AddSingleton<ApiDiagnostics>();

    var app = builder.Build();

    // Temporary startup diagnostic - prints on every boot, regardless of the
    // Logging:LogLevel config, so a config-resolution problem (env var/user
    // secret silently overriding appsettings.*.json) is visible immediately in
    // the console/Logs/platform-api.log without needing to trigger a request
    // first. Reads "BusinessApiInternal:BaseUrl" (renamed from "BusinessApi:
    // BaseUrl" - see PlatformInfrastructureRegistration for why). Logs a short
    // SHA-256 fingerprint of the ServiceKey (not the value) - a length-only
    // check can't tell two different same-length keys apart, which is exactly
    // the ambiguity this replaces. Compare this fingerprint against
    // RenewalTracker.Api's own boot line - identical fingerprint = identical
    // key. Safe to remove once confirmed stable.
    app.Logger.LogWarning(
        "STARTUP CONFIG CHECK -> Environment={Environment} | BusinessApiInternal:BaseUrl={BusinessApiBaseUrl} | Internal:ServiceKey fingerprint={ServiceKeyFingerprint}",
        app.Environment.EnvironmentName,
        app.Configuration["BusinessApiInternal:BaseUrl"],
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(app.Configuration["Internal:ServiceKey"] ?? string.Empty)))[..8]);

    // ---------------------------------------------------------------------
    // Startup report: where the logs are, and whether everything the API needs is in place.
    // Problems are logged as warnings, so they appear at the top of Logs/errors.log.
    // ---------------------------------------------------------------------
    app.Logger.LogInformation("API starting - environment {Environment}, logs folder {LogsFolder}",
        app.Environment.EnvironmentName, ApiDiagnostics.LogsFolder);
    {
        var smtp = SmtpMail.Read(app.Configuration);
        var wa = app.Services.GetRequiredService<WhatsAppCloud>().Read();
        app.Logger.LogInformation(
            "NOTIFICATIONS -> Email: {Email} (host {Host}:{Port}, from {From}, leads to {NotifyEmail}, visitor acknowledgement {Ack}) | WhatsApp: {WhatsApp} (staff numbers {StaffCount}, staff template '{StaffTemplate}', customer template '{CustomerTemplate}')",
            smtp.IsConfigured ? "ON" : "OFF - set Smtp:Host/UserName/Password", smtp.Host, smtp.Port, smtp.FromEmail,
            string.IsNullOrWhiteSpace(app.Configuration["Website:NotifyEmail"]) ? "(per-form NotifyEmail only)" : app.Configuration["Website:NotifyEmail"],
            bool.TryParse(app.Configuration["Website:SendAcknowledgement"], out var ack) && !ack ? "off" : "on",
            wa.IsConfigured ? "ON" : (wa.Enabled ? "OFF - set WhatsApp:PhoneNumberId/AccessToken" : "OFF"),
            wa.StaffNumbers.Count, wa.StaffTemplate.Name, wa.CustomerTemplate.Name);
    }
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        app.Logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception - the API process is stopping");
    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        app.Logger.LogError(e.Exception, "Unobserved background task exception");
        e.SetObserved();
    };
    _ = Task.Run(async () =>
    {
        try
        {
            var report = await app.Services.GetRequiredService<ApiDiagnostics>().RunAsync();
            if (report.Healthy)
                app.Logger.LogInformation(
                    "STARTUP CHECK OK -> database {Server}/{Database} reachable, Website Leads objects present ({Forms} active forms), website origins: {Origins}",
                    report.DatabaseServer, report.DatabaseName, report.ActiveWebsiteForms, string.Join(", ", report.WebsiteAllowedOrigins));
            else
                foreach (var problem in report.Problems)
                    app.Logger.LogWarning("STARTUP CHECK PROBLEM -> {Problem}", problem);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Startup check could not run");
        }
    });
    app.Lifetime.ApplicationStopping.Register(() => app.Logger.LogInformation("API stopping"));

    // Swagger: always in Development; in Production only when "Swagger:Enabled": true
    if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Swagger:Enabled", false))
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    // Outermost, so it wraps ExceptionHandlingMiddleware and always logs the
    // true final status code, including a request that failed.
    app.UseMiddleware<RequestResponseLoggingMiddleware>();
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    // Local runs: no http -> https redirect. A browser will not follow a redirect on a CORS pre-check,
    // so the local website (http://localhost:5291) would otherwise be blocked. Production is unchanged.
    if (!app.Environment.IsDevelopment())
        app.UseHttpsRedirection();
    // Default policy for the Admin Panel; PublicWebFormsController overrides it
    // with [EnableCors("Website")] (endpoint metadata wins over this default).
    app.UseCors("PlatformAdminClient");
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapHub<WebLeadsHub>(WebLeadsHub.Path);

    // Root (no auth): opening https://adminpanelapi.givingsign.in/ in a browser goes to Swagger
    // when it is enabled, otherwise shows a short status instead of a bare 401.
    var swaggerOn = app.Environment.IsDevelopment() || app.Configuration.GetValue("Swagger:Enabled", false);
    app.MapGet("/", (IWebHostEnvironment env) => swaggerOn
            ? Results.Redirect("/swagger")
            : Results.Ok(new { service = "RenewalTracker.PlatformApi", status = "ok", environment = env.EnvironmentName }))
        .AllowAnonymous()
        .ExcludeFromDescription();

    // Health check (no auth - bypasses the FallbackPolicy): https://adminpanelapi.givingsign.in/health
    // https://adminpanelapi.givingsign.in/health?check=true also reports, without secrets, whether the
    // logs can be written and whether the database answers - for when the logs themselves are missing.
    app.MapGet("/health", async (IWebHostEnvironment env, bool? check) =>
            check == true
                ? Results.Ok(new { status = "ok", environment = env.EnvironmentName, time = DateTime.Now,
                                   checks = await app.Services.GetRequiredService<ApiDiagnostics>().QuickCheckAsync() })
                : Results.Ok(new { status = "ok", environment = env.EnvironmentName, time = DateTime.Now }))
        .AllowAnonymous();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // Write the real startup exception next to the exe so it can be read from Plesk File Manager.
    try
    {
        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "startup-error.txt"),
            $"{DateTime.Now:O}{Environment.NewLine}{ex}");
        // ...and into Logs/errors.log, where every other problem is
        var logs = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(logs);
        File.AppendAllText(Path.Combine(logs, "errors.log"),
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} FATAL Startup - the API could not start: {ex}{Environment.NewLine}");
    }
    catch
    {
        // no write permission - the stdout log (logs\stdout_*.log) still has it
    }

    Console.Error.WriteLine(ex);
    throw;
}
