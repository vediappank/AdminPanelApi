using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace RenewalTracker.PlatformApi.Middleware;

/// <summary>
/// Converts EF Core / SQL Server exceptions that reach a controller into
/// clean JSON error responses instead of an unhandled 500 with a raw stack
/// trace - same behavior as RenewalTracker.Api's middleware of the same
/// name (duplicated rather than shared via Infrastructure, since ASP.NET
/// Core middleware belongs to a Web SDK project and each API project
/// already has its own Middleware folder). Every foreign key in
/// PlatformDbContext is DeleteBehavior.Restrict, so SQL Server already
/// refuses a blocked delete (e.g. deleting a Module still referenced by a
/// Permission) - this middleware only makes that refusal readable.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException sqlEx)
        {
            await WriteSqlErrorAsync(context, sqlEx, ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteErrorAsync(context, HttpStatusCode.InternalServerError, "An unexpected error occurred. Please try again or contact support.");
        }
    }

    private async Task WriteSqlErrorAsync(HttpContext context, SqlException sqlEx, DbUpdateException outer)
    {
        switch (sqlEx.Number)
        {
            case 547: // FK constraint violation - blocked delete (child rows exist) or invalid parent reference on insert/update
                _logger.LogWarning(outer, "Foreign key constraint violation");
                var isDelete = HttpMethods.IsDelete(context.Request.Method);
                await WriteErrorAsync(context, HttpStatusCode.Conflict, BuildForeignKeyMessage(sqlEx.Message, isDelete));
                return;
            case 2601:
            case 2627: // unique constraint / unique index violation
                _logger.LogWarning(outer, "Unique constraint violation");
                await WriteErrorAsync(context, HttpStatusCode.Conflict, "A record with the same value already exists.");
                return;
            default:
                _logger.LogError(outer, "Unhandled SQL exception {Number}: {Message}", sqlEx.Number, sqlEx.Message);
                await WriteErrorAsync(context, HttpStatusCode.InternalServerError, "An unexpected database error occurred.");
                return;
        }
    }

    private static string BuildForeignKeyMessage(string sqlMessage, bool isDelete)
    {
        var match = Regex.Match(sqlMessage, "table \"dbo\\.(\\w+)\"");
        if (!match.Success)
        {
            return isDelete
                ? "Cannot delete this record because it is referenced by other records."
                : "This record refers to a value that does not exist.";
        }

        var tableName = match.Groups[1].Value;
        return isDelete
            ? $"Cannot delete this record because existing {tableName} records depend on it."
            : $"This request refers to a {tableName} record that does not exist.";
    }

    private static Task WriteErrorAsync(HttpContext context, HttpStatusCode status, string message)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)status;
        var payload = JsonSerializer.Serialize(new { message });
        return context.Response.WriteAsync(payload);
    }
}
