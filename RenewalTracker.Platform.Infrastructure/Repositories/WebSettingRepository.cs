using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using RenewalTracker.Platform.Application.DTOs.WebSettings;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.Platform.Infrastructure.Repositories;

/// <summary>Dapper over dbo.usp_WebSetting_* - same "DefaultConnection" (SuperAdminPanel) as WebLeadRepository.</summary>
public class WebSettingRepository : IWebSettingRepository
{
    private const int CommandTimeoutSeconds = 30;
    private readonly string _connectionString;

    public WebSettingRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    }

    public async Task<List<WebSettingDto>> ListAsync(bool publicOnly, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_connectionString);
        var rows = await cn.QueryAsync<WebSettingDto>(new CommandDefinition("dbo.usp_WebSetting_List", new { PublicOnly = publicOnly },
            commandType: CommandType.StoredProcedure, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task SaveAsync(IEnumerable<WebSettingValueDto> items, string changedBy, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            foreach (var item in items)
            {
                await cn.ExecuteAsync(new CommandDefinition("dbo.usp_WebSetting_Save", new
                {
                    SettingKey = item.SettingKey,
                    SettingValue = string.IsNullOrWhiteSpace(item.SettingValue) ? null : item.SettingValue.Trim(),
                    ChangedBy = changedBy.Length > 100 ? changedBy[..100] : changedBy,
                }, tx, CommandTimeoutSeconds, CommandType.StoredProcedure, cancellationToken: ct));
            }
            await tx.CommitAsync(ct);
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw new WebLeadException(ex.Number == 50404 ? 404 : 400, ex.Message, ex);
        }
    }
}
