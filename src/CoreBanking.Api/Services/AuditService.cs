using System.Text.Json;
using CoreBanking.Api.Data;
using CoreBanking.Api.Entities;

namespace CoreBanking.Api.Services;

/// <summary>
/// Append-only audit trail. Every mutating flow must leave a trace here.
/// Payloads never include secrets, passwords, or token material.
/// </summary>
public class AuditService
{
    private readonly AppDbContext _db;

    public AuditService(AppDbContext db) => _db = db;

    public void Write(Guid? userId, string action, string entityName, string? entityId,
        object? payload, string? ipAddress)
    {
        string? json = null;
        if (payload is not null)
        {
            try { json = JsonSerializer.Serialize(payload); }
            catch { json = "{\"note\":\"payload serialization failed\"}"; }
        }

        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            EntityName = entityName,
            EntityId = entityId,
            Payload = json,
            IpAddress = ipAddress,
            CreatedAt = DateTime.UtcNow
        });
        // Saved together with the caller's unit of work.
    }
}
