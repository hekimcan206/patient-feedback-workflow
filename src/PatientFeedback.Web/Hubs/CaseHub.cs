using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PatientFeedback.Cases;
using PatientFeedback.Oracle;

namespace PatientFeedback.Web.Hubs;

/// <summary>
/// Live channel of the case-tracking panel. Server-to-client only: changes are made through the MVC actions (which
/// validate, authorise and audit), and the hub broadcasts the result. Payloads never carry personal data.
/// </summary>
[Authorize]
public sealed class CaseHub : Hub
{
    public const string Path = "/hubs/cases";
}

/// <summary>What the panel grid shows for a case.</summary>
public sealed record CaseDto(
    long CaseId,
    string Department,
    string Severity,
    string Status,
    string Reasons,
    DateTimeOffset OpenedAt,
    DateTimeOffset SlaDueAt,
    string? AssignedTo,
    bool SlaBreached,
    int? Nps,
    int? Csat,
    int? Ces,
    bool HasComment)
{
    public static CaseDto From(CaseRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new CaseDto(
            row.CaseId,
            row.Department,
            ((CaseSeverity)row.Severity).ToString(),
            row.Status,
            row.Reasons,
            row.OpenedAt,
            row.SlaDueAt,
            row.AssignedTo,
            row.IsBreached,
            row.Nps,
            row.Csat,
            row.Ces,
            row.HasComment);
    }
}

public sealed class CaseNotifier(IHubContext<CaseHub> hub, CaseRepository cases, ILogger<CaseNotifier> logger)
{
    public const string CaseUpserted = "caseUpserted";
    public const string ResponseRecorded = "responseRecorded";

    public async Task CaseChangedAsync(long caseId, CancellationToken ct = default)
    {
        try
        {
            if (await cases.GetAsync(caseId, ct) is { } row)
            {
                await hub.Clients.All.SendAsync(CaseUpserted, CaseDto.From(row), ct);
            }
        }
#pragma warning disable CA1031 // A failed push must never fail the business operation that already committed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogWarning(ex, "Live update for case {CaseId} failed; clients will catch up on their next refresh", caseId);
        }
    }

    public Task SurveyAnsweredAsync(string department, int nps, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync(ResponseRecorded, new { department, nps }, ct);
}
