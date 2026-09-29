namespace VspcAutotaskPlugin.Domain;

// ---------------------------------------------------------------------------
// Connection settings (stored in the settings table; *Enc fields hold values
// encrypted by SecretProtector, never plaintext).
// ---------------------------------------------------------------------------

/// <summary>
/// VSPC REST connection, auto-provisioned by the VSPC plugin host at registration time
/// (discoverService + getApiKey). Never entered manually.
/// </summary>
public sealed class VspcConnectionSettings
{
    /// <summary>Discovered loopback REST endpoint, e.g. https://localhost:1280</summary>
    public string BaseUrl { get; set; } = "";
    public string? ApiKeyEnc { get; set; }
    /// <summary>The loopback endpoint typically presents a VSPC-managed self-signed certificate.</summary>
    public bool IgnoreTlsErrors { get; set; } = true;
}

public sealed class AutotaskConnectionSettings
{
    public string Username { get; set; } = "";
    public string? SecretEnc { get; set; }
    public string IntegrationCode { get; set; } = "";
    /// <summary>Cached zone base URL, e.g. https://webservices3.autotask.net/atservicesrest/</summary>
    public string? ZoneUrl { get; set; }
}

public sealed class FeatureToggles
{
    public bool Companies { get; set; }
    public bool Billing { get; set; }
    public bool Ticketing { get; set; }
}

/// <summary>Mirrors the ConnectWise Manage plugin's ticket settings (board → queue, delay, per-severity priorities).</summary>
public sealed class TicketingSettings
{
    public int? QueueId { get; set; }
    public int? NewStatusId { get; set; }
    public int? CompleteStatusId { get; set; }
    public int? WarningPriorityId { get; set; }
    public int? ErrorPriorityId { get; set; }
    public int? SourceId { get; set; }
    public int? TicketTypeId { get; set; }
    /// <summary>Wait between alarm trigger and ticket creation; alarms that resolve within the window never become tickets.</summary>
    public int DelayMinutes { get; set; } = 5;
    /// <summary>Autotask requires dueDateTime on most ticket categories; due = creation + DueHours.</summary>
    public int DueHours { get; set; } = 24;
    public int PollSeconds { get; set; } = 120;
    public bool CloseTicketOnAlarmResolve { get; set; } = true;
    public bool ResolveAlarmOnTicketClose { get; set; } = true;
    public bool NoteOnRetrigger { get; set; } = true;
    /// <summary>Treat Acknowledged alarms like Resolved ones when closing tickets (CWM parity).</summary>
    public bool AcknowledgeClosesTicket { get; set; } = true;
    /// <summary>Set when ticketing is first enabled; alarms triggered before this never create tickets.</summary>
    public DateTimeOffset? EnabledAt { get; set; }
}

public sealed class BillingSettings
{
    /// <summary>VSPC subscription plan used as the pricing reference when creating Autotask services.</summary>
    public string? SubscriptionPlanUid { get; set; }
    /// <summary>Day of month the billing period starts (1-28).</summary>
    public int AnchorDayOfMonth { get; set; } = 1;
    /// <summary>Hour (UTC) of the automatic daily billing sync.</summary>
    public int SyncHourUtc { get; set; } = 1;
    /// <summary>Autotask Services.periodType picklist value used when creating services (instance-specific).</summary>
    public int? DefaultPeriodType { get; set; }
    /// <summary>Autotask billing code (allocation code) used when creating services.</summary>
    public long? DefaultBillingCodeId { get; set; }
}

// ---------------------------------------------------------------------------
// Mapping rows
// ---------------------------------------------------------------------------

public sealed record CompanyMapping(
    string VspcCompanyUid,
    string VspcCompanyName,
    long AtCompanyId,
    string AtCompanyName,
    bool Auto,
    string MappedAt);

public enum ServiceMapMode { Skip, Existing, CreateNew }

public sealed record ServiceMapping(
    string ServiceKey,
    ServiceMapMode Mode,
    long? AtServiceId,
    string? AtServiceName,
    double? UnitPrice,
    long? BillingCodeId,
    int? PeriodType,
    string UpdatedAt);

public sealed record CompanyBilling(
    string VspcCompanyUid,
    long AtContractId,
    string? AtContractName,
    List<string> EnabledServices,
    string UpdatedAt);

public sealed record AlarmRule(
    string AlarmTemplateUid,
    bool Enabled,
    string? Name,
    string? Category,
    long? InternalId);

// ---------------------------------------------------------------------------
// Ticket lifecycle
// ---------------------------------------------------------------------------

public enum TicketLinkState { Pending, Open, Closed, Cancelled, Error }

/// <summary>One row per VSPC triggered alarm (active alarm UID) that the ticket engine has seen and acted on.</summary>
public sealed class TicketLink
{
    public string ActiveAlarmUid { get; set; } = "";
    public string? VspcCompanyUid { get; set; }
    public string? VspcCompanyName { get; set; }
    public string? AlarmTemplateUid { get; set; }
    public string? AlarmName { get; set; }
    public string? ObjectName { get; set; }
    public long? AtCompanyId { get; set; }
    public long? AtTicketId { get; set; }
    public string? AtTicketNumber { get; set; }
    public TicketLinkState State { get; set; } = TicketLinkState.Pending;
    public string? LastStatus { get; set; }
    public string? LastActivationTime { get; set; }
    public int RepeatCount { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public string? LastError { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed record BillingAdjustmentEntry(
    long Id,
    string Ts,
    string VspcCompanyUid,
    string? VspcCompanyName,
    string ServiceKey,
    long? AtContractId,
    long? AtServiceId,
    long? PrevUnits,
    long? NewUnits,
    long? Delta,
    string? EffectiveDate,
    bool DryRun,
    string? Result);
