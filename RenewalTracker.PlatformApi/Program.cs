using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using RenewalTracker.Platform.Infrastructure.Auth;
using RenewalTracker.Platform.Infrastructure.DependencyInjection;
using RenewalTracker.PlatformApi.Json;
using RenewalTracker.PlatformApi.Middleware;

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Outermost, so it wraps ExceptionHandlingMiddleware and always logs the
// true final status code, including a request that failed.
app.UseMiddleware<RequestResponseLoggingMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseCors("PlatformAdminClient");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
