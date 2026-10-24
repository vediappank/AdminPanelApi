using System.Data;
using Microsoft.Data.SqlClient;
using RenewalTracker.Platform.Infrastructure.Services;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Settings ▸ Email &amp; WhatsApp - one row in SuperAdminPanel.dbo.PlatformMessagingSettings, plus each tenant's
/// automatic-reminder switches in dbo.TenantMessagingSettings. The Business Panel API reads both (PlatformMessaging)
/// to send every email from this mailbox and the reminders by email / WhatsApp.
/// Passwords and API keys are stored encrypted with TenantDatabase:CredentialKey (same key as MultiTenant:CredentialKey
/// of the Business Panel API); without a key they are stored as "plain:..." and the screen warns about it.
/// The tables are created here on first use (also in DatabaseScripts/Messaging_SuperAdminPanel.sql).
/// </summary>
public sealed class PlatformMessagingStore
{
    public const string EnsureSql = @"
IF OBJECT_ID(N'dbo.PlatformMessagingSettings', N'U') IS NULL
CREATE TABLE dbo.PlatformMessagingSettings
(
    SettingsId               INT            NOT NULL CONSTRAINT PK_PlatformMessagingSettings PRIMARY KEY,
    EmailEnabled             BIT            NOT NULL CONSTRAINT DF_PMS_EmailEnabled DEFAULT (0),
    SmtpHost                 NVARCHAR(200)  NULL,
    SmtpPort                 INT            NOT NULL CONSTRAINT DF_PMS_SmtpPort DEFAULT (587),
    SmtpEnableSsl            BIT            NOT NULL CONSTRAINT DF_PMS_SmtpSsl DEFAULT (1),
    SmtpUserName             NVARCHAR(200)  NULL,
    SmtpPassword             NVARCHAR(1000) NULL,
    FromEmail                NVARCHAR(200)  NULL,
    FromName                 NVARCHAR(200)  NULL,
    WhatsAppEnabled          BIT            NOT NULL CONSTRAINT DF_PMS_WaEnabled DEFAULT (0),
    WhatsAppProvider         NVARCHAR(20)   NOT NULL CONSTRAINT DF_PMS_WaProvider DEFAULT (N'meta'),
    WhatsAppApiUrl           NVARCHAR(400)  NULL,
    WhatsAppAccountId        NVARCHAR(200)  NULL,
    WhatsAppApiKey           NVARCHAR(2000) NULL,
    WhatsAppSender           NVARCHAR(30)   NULL,
    WhatsAppTemplate         NVARCHAR(200)  NULL,
    WhatsAppTemplateLanguage NVARCHAR(20)   NULL,
    DefaultCountryCode       NVARCHAR(5)    NULL,
    RemindersEnabled         BIT            NOT NULL CONSTRAINT DF_PMS_Reminders DEFAULT (0),
    RemindersRunHour         INT            NOT NULL CONSTRAINT DF_PMS_RunHour DEFAULT (8),
    ModifiedDate             DATETIME2(0)   NULL,
    ModifiedBy               NVARCHAR(200)  NULL
);
IF OBJECT_ID(N'dbo.TenantMessagingSettings', N'U') IS NULL
CREATE TABLE dbo.TenantMessagingSettings
(
    TenantId     INT           NOT NULL CONSTRAINT PK_TenantMessagingSettings PRIMARY KEY
                               CONSTRAINT FK_TenantMessagingSettings_Tenants REFERENCES dbo.Tenants (TenantId) ON DELETE CASCADE,
    AutoEmail    BIT           NOT NULL CONSTRAINT DF_TMS_AutoEmail DEFAULT (0),
    AutoWhatsApp BIT           NOT NULL CONSTRAINT DF_TMS_AutoWhatsApp DEFAULT (0),
    ModifiedDate DATETIME2(0)  NULL,
    ModifiedBy   NVARCHAR(200) NULL
);
-- 24/7 support button of the Business Panel
IF COL_LENGTH(N'dbo.PlatformMessagingSettings', N'SupportEnabled') IS NULL
    ALTER TABLE dbo.PlatformMessagingSettings ADD SupportEnabled BIT NOT NULL CONSTRAINT DF_PMS_SupportEnabled DEFAULT (0);
IF COL_LENGTH(N'dbo.PlatformMessagingSettings', N'SupportWhatsApp') IS NULL
    ALTER TABLE dbo.PlatformMessagingSettings ADD SupportWhatsApp NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.PlatformMessagingSettings', N'SupportEmail') IS NULL
    ALTER TABLE dbo.PlatformMessagingSettings ADD SupportEmail NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.PlatformMessagingSettings', N'SupportHours') IS NULL
    ALTER TABLE dbo.PlatformMessagingSettings ADD SupportHours NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.PlatformMessagingSettings', N'SupportGreeting') IS NULL
    ALTER TABLE dbo.PlatformMessagingSettings ADD SupportGreeting NVARCHAR(300) NULL;
-- requests sent from the support button (e-mailed to SupportEmail and kept here)
IF OBJECT_ID(N'dbo.SupportRequests', N'U') IS NULL
CREATE TABLE dbo.SupportRequests
(
    SupportRequestId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupportRequests PRIMARY KEY,
    TenantId         INT            NULL,
    CompanyName      NVARCHAR(200)  NULL,
    UserName         NVARCHAR(200)  NULL,
    UserEmail        NVARCHAR(200)  NULL,
    Phone            NVARCHAR(30)   NULL,
    Subject          NVARCHAR(200)  NOT NULL,
    Message          NVARCHAR(4000) NOT NULL,
    PageUrl          NVARCHAR(400)  NULL,
    Status           NVARCHAR(20)   NOT NULL CONSTRAINT DF_SupportRequests_Status DEFAULT (N'Open'),
    EmailSent        BIT            NOT NULL CONSTRAINT DF_SupportRequests_Emailed DEFAULT (0),
    CreatedDate      DATETIME2(0)   NOT NULL CONSTRAINT DF_SupportRequests_Created DEFAULT (SYSUTCDATETIME()),
    ClosedDate       DATETIME2(0)   NULL,
    ClosedBy         NVARCHAR(200)  NULL,
    Resolution       NVARCHAR(2000) NULL
);";

    public sealed class Settings
    {
        public bool EmailEnabled { get; set; }
        public string? SmtpHost { get; set; }
        public int SmtpPort { get; set; } = 587;
        public bool SmtpEnableSsl { get; set; } = true;
        public string? SmtpUserName { get; set; }
        public string? SmtpPassword { get; set; }
        public string? FromEmail { get; set; }
        public string? FromName { get; set; }
        public bool WhatsAppEnabled { get; set; }
        public string WhatsAppProvider { get; set; } = "meta";
        public string? WhatsAppApiUrl { get; set; }
        public string? WhatsAppAccountId { get; set; }
        public string? WhatsAppApiKey { get; set; }
        public string? WhatsAppSender { get; set; }
        public string? WhatsAppTemplate { get; set; }
        public string? WhatsAppTemplateLanguage { get; set; }
        public string? DefaultCountryCode { get; set; }
        public bool RemindersEnabled { get; set; }
        public int RemindersRunHour { get; set; } = 8;
        public bool SupportEnabled { get; set; }
        public string? SupportWhatsApp { get; set; }
        public string? SupportEmail { get; set; }
        public string? SupportHours { get; set; }
        public string? SupportGreeting { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }
        /// <summary>Stored row found.</summary>
        public bool Saved { get; set; }
        /// <summary>A secret was stored that cannot be decrypted with this server's key.</summary>
        public bool SecretUnreadable { get; set; }

        public SmtpMail.Settings? ToSmtp() =>
            !EmailEnabled || string.IsNullOrWhiteSpace(SmtpHost) ? null : new SmtpMail.Settings(
                SmtpHost.Trim(), SmtpPort == 465 ? 587 : SmtpPort <= 0 ? 587 : SmtpPort, SmtpEnableSsl,
                (SmtpUserName ?? string.Empty).Trim(), SmtpPassword ?? string.Empty,
                string.IsNullOrWhiteSpace(FromEmail) ? (SmtpUserName ?? string.Empty).Trim() : FromEmail.Trim(),
                string.IsNullOrWhiteSpace(FromName) ? SmtpMail.CompanyName : FromName.Trim());

        public WhatsAppConfig ToWhatsApp() => new()
        {
            Enabled = WhatsAppEnabled,
            Provider = string.IsNullOrWhiteSpace(WhatsAppProvider) ? "meta" : WhatsAppProvider.Trim().ToLowerInvariant(),
            ApiUrl = WhatsAppApiUrl, AccountId = WhatsAppAccountId, ApiKey = WhatsAppApiKey, Sender = WhatsAppSender,
            TemplateName = WhatsAppTemplate,
            TemplateLanguage = string.IsNullOrWhiteSpace(WhatsAppTemplateLanguage) ? "en" : WhatsAppTemplateLanguage.Trim(),
            DefaultCountryCode = string.IsNullOrWhiteSpace(DefaultCountryCode) ? "971" : DefaultCountryCode.Trim(),
        };
    }

    private readonly string _connection;
    private readonly string _key;
    private static bool _ensured;

    public PlatformMessagingStore(IConfiguration config)
    {
        _connection = config.GetConnectionString("DefaultConnection") ?? string.Empty;
        _key = (config["TenantDatabase:CredentialKey"] ?? string.Empty).Trim();
    }

    /// <summary>TenantDatabase:CredentialKey is set - secrets are stored encrypted.</summary>
    public bool CanEncrypt => _key.Length > 0;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_connection);
        await cn.OpenAsync(ct);
        if (!_ensured)
        {
            await using var ensure = new SqlCommand(EnsureSql, cn);
            await ensure.ExecuteNonQueryAsync(ct);
            _ensured = true;
        }
        return cn;
    }

    public async Task<Settings> GetAsync(CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT TOP (1) * FROM dbo.PlatformMessagingSettings ORDER BY SettingsId", cn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var s = new Settings();
        if (!await r.ReadAsync(ct)) return s;
        string? Str(string c) => r[c] is DBNull ? null : Convert.ToString(r[c]);
        s.Saved = true;
        s.EmailEnabled = (bool)r["EmailEnabled"];
        s.SmtpHost = Str("SmtpHost");
        s.SmtpPort = Convert.ToInt32(r["SmtpPort"]);
        s.SmtpEnableSsl = (bool)r["SmtpEnableSsl"];
        s.SmtpUserName = Str("SmtpUserName");
        s.SmtpPassword = Unprotect(Str("SmtpPassword"), s);
        s.FromEmail = Str("FromEmail");
        s.FromName = Str("FromName");
        s.WhatsAppEnabled = (bool)r["WhatsAppEnabled"];
        s.WhatsAppProvider = Str("WhatsAppProvider") ?? "meta";
        s.WhatsAppApiUrl = Str("WhatsAppApiUrl");
        s.WhatsAppAccountId = Str("WhatsAppAccountId");
        s.WhatsAppApiKey = Unprotect(Str("WhatsAppApiKey"), s);
        s.WhatsAppSender = Str("WhatsAppSender");
        s.WhatsAppTemplate = Str("WhatsAppTemplate");
        s.WhatsAppTemplateLanguage = Str("WhatsAppTemplateLanguage");
        s.DefaultCountryCode = Str("DefaultCountryCode");
        s.RemindersEnabled = (bool)r["RemindersEnabled"];
        s.RemindersRunHour = Convert.ToInt32(r["RemindersRunHour"]);
        s.ModifiedDate = r["ModifiedDate"] is DBNull ? null : DateTime.SpecifyKind(Convert.ToDateTime(r["ModifiedDate"]), DateTimeKind.Utc);
        s.ModifiedBy = Str("ModifiedBy");
        s.SupportEnabled = r["SupportEnabled"] is bool se && se;
        s.SupportWhatsApp = Str("SupportWhatsApp");
        s.SupportEmail = Str("SupportEmail");
        s.SupportHours = Str("SupportHours");
        s.SupportGreeting = Str("SupportGreeting");
        return s;
    }

    public async Task SaveAsync(Settings s, string? by, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
MERGE dbo.PlatformMessagingSettings AS t
USING (SELECT 1 AS SettingsId) AS x ON t.SettingsId = x.SettingsId
WHEN MATCHED THEN UPDATE SET
    EmailEnabled = @EmailEnabled, SmtpHost = @SmtpHost, SmtpPort = @SmtpPort, SmtpEnableSsl = @SmtpEnableSsl, SmtpUserName = @SmtpUserName,
    SmtpPassword = @SmtpPassword, FromEmail = @FromEmail, FromName = @FromName,
    WhatsAppEnabled = @WhatsAppEnabled, WhatsAppProvider = @WhatsAppProvider, WhatsAppApiUrl = @WhatsAppApiUrl, WhatsAppAccountId = @WhatsAppAccountId,
    WhatsAppApiKey = @WhatsAppApiKey, WhatsAppSender = @WhatsAppSender, WhatsAppTemplate = @WhatsAppTemplate, WhatsAppTemplateLanguage = @WhatsAppTemplateLanguage,
    DefaultCountryCode = @DefaultCountryCode, RemindersEnabled = @RemindersEnabled, RemindersRunHour = @RemindersRunHour,
    SupportEnabled = @SupportEnabled, SupportWhatsApp = @SupportWhatsApp, SupportEmail = @SupportEmail, SupportHours = @SupportHours, SupportGreeting = @SupportGreeting,
    ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @By
WHEN NOT MATCHED THEN INSERT (SettingsId, EmailEnabled, SmtpHost, SmtpPort, SmtpEnableSsl, SmtpUserName, SmtpPassword, FromEmail, FromName,
    WhatsAppEnabled, WhatsAppProvider, WhatsAppApiUrl, WhatsAppAccountId, WhatsAppApiKey, WhatsAppSender, WhatsAppTemplate, WhatsAppTemplateLanguage,
    DefaultCountryCode, RemindersEnabled, RemindersRunHour, SupportEnabled, SupportWhatsApp, SupportEmail, SupportHours, SupportGreeting, ModifiedDate, ModifiedBy)
VALUES (1, @EmailEnabled, @SmtpHost, @SmtpPort, @SmtpEnableSsl, @SmtpUserName, @SmtpPassword, @FromEmail, @FromName,
    @WhatsAppEnabled, @WhatsAppProvider, @WhatsAppApiUrl, @WhatsAppAccountId, @WhatsAppApiKey, @WhatsAppSender, @WhatsAppTemplate, @WhatsAppTemplateLanguage,
    @DefaultCountryCode, @RemindersEnabled, @RemindersRunHour, @SupportEnabled, @SupportWhatsApp, @SupportEmail, @SupportHours, @SupportGreeting, SYSUTCDATETIME(), @By);", cn);
        void P(string name, SqlDbType type, object? value, int size = 0)
        {
            var p = size > 0 ? cmd.Parameters.Add(name, type, size) : cmd.Parameters.Add(name, type);
            p.Value = value ?? DBNull.Value;
        }
        P("@EmailEnabled", SqlDbType.Bit, s.EmailEnabled);
        P("@SmtpHost", SqlDbType.NVarChar, Clean(s.SmtpHost), 200);
        P("@SmtpPort", SqlDbType.Int, s.SmtpPort <= 0 ? 587 : s.SmtpPort);
        P("@SmtpEnableSsl", SqlDbType.Bit, s.SmtpEnableSsl);
        P("@SmtpUserName", SqlDbType.NVarChar, Clean(s.SmtpUserName), 200);
        P("@SmtpPassword", SqlDbType.NVarChar, Protect(s.SmtpPassword), 1000);
        P("@FromEmail", SqlDbType.NVarChar, Clean(s.FromEmail), 200);
        P("@FromName", SqlDbType.NVarChar, Clean(s.FromName), 200);
        P("@WhatsAppEnabled", SqlDbType.Bit, s.WhatsAppEnabled);
        P("@WhatsAppProvider", SqlDbType.NVarChar, Clean(s.WhatsAppProvider)?.ToLowerInvariant() ?? "meta", 20);
        P("@WhatsAppApiUrl", SqlDbType.NVarChar, Clean(s.WhatsAppApiUrl), 400);
        P("@WhatsAppAccountId", SqlDbType.NVarChar, Clean(s.WhatsAppAccountId), 200);
        P("@WhatsAppApiKey", SqlDbType.NVarChar, Protect(s.WhatsAppApiKey), 2000);
        P("@WhatsAppSender", SqlDbType.NVarChar, Clean(s.WhatsAppSender), 30);
        P("@WhatsAppTemplate", SqlDbType.NVarChar, Clean(s.WhatsAppTemplate), 200);
        P("@WhatsAppTemplateLanguage", SqlDbType.NVarChar, Clean(s.WhatsAppTemplateLanguage), 20);
        P("@DefaultCountryCode", SqlDbType.NVarChar, Clean(s.DefaultCountryCode), 5);
        P("@RemindersEnabled", SqlDbType.Bit, s.RemindersEnabled);
        P("@RemindersRunHour", SqlDbType.Int, Math.Clamp(s.RemindersRunHour, 0, 23));
        P("@SupportEnabled", SqlDbType.Bit, s.SupportEnabled);
        P("@SupportWhatsApp", SqlDbType.NVarChar, Clean(s.SupportWhatsApp), 30);
        P("@SupportEmail", SqlDbType.NVarChar, Clean(s.SupportEmail), 200);
        P("@SupportHours", SqlDbType.NVarChar, Clean(s.SupportHours), 100);
        P("@SupportGreeting", SqlDbType.NVarChar, Clean(s.SupportGreeting), 300);
        P("@By", SqlDbType.NVarChar, by, 200);
        await cmd.ExecuteNonQueryAsync(ct);
        SmtpMail.PlatformOverride = s.ToSmtp();
    }

    public async Task<(bool AutoEmail, bool AutoWhatsApp, DateTime? ModifiedDate, string? ModifiedBy)> GetTenantAsync(int tenantId, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT AutoEmail, AutoWhatsApp, ModifiedDate, ModifiedBy FROM dbo.TenantMessagingSettings WHERE TenantId = @id", cn);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = tenantId;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (false, false, null, null);
        return ((bool)r[0], (bool)r[1], r[2] is DBNull ? null : DateTime.SpecifyKind(Convert.ToDateTime(r[2]), DateTimeKind.Utc), r[3] as string);
    }

    public async Task SaveTenantAsync(int tenantId, bool autoEmail, bool autoWhatsApp, string? by, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"
MERGE dbo.TenantMessagingSettings AS t
USING (SELECT @id AS TenantId) AS x ON t.TenantId = x.TenantId
WHEN MATCHED THEN UPDATE SET AutoEmail = @email, AutoWhatsApp = @wa, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @by
WHEN NOT MATCHED THEN INSERT (TenantId, AutoEmail, AutoWhatsApp, ModifiedDate, ModifiedBy) VALUES (@id, @email, @wa, SYSUTCDATETIME(), @by);", cn);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = tenantId;
        cmd.Parameters.Add("@email", SqlDbType.Bit).Value = autoEmail;
        cmd.Parameters.Add("@wa", SqlDbType.Bit).Value = autoWhatsApp;
        cmd.Parameters.Add("@by", SqlDbType.NVarChar, 200).Value = (object?)by ?? DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        return _key.Length > 0 ? TenantDbSecret.Protect(secret, _key) : "plain:" + secret;
    }

    private string? Unprotect(string? stored, Settings s)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (stored.StartsWith("plain:", StringComparison.Ordinal)) return stored[6..];
        if (_key.Length == 0) { s.SecretUnreadable = true; return null; }
        try { return TenantDbSecret.Unprotect(stored, _key); }
        catch { s.SecretUnreadable = true; return null; }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Keeps <see cref="SmtpMail.PlatformOverride"/> (the mailbox of Settings ▸ Email &amp; WhatsApp) up to date for this API's own emails.</summary>
public sealed class PlatformMessagingRefresher : BackgroundService
{
    private readonly PlatformMessagingStore _store;
    private readonly ILogger<PlatformMessagingRefresher> _logger;

    public PlatformMessagingRefresher(PlatformMessagingStore store, ILogger<PlatformMessagingRefresher> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { SmtpMail.PlatformOverride = (await _store.GetAsync(stoppingToken)).ToSmtp(); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Email & WhatsApp settings could not be read."); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
