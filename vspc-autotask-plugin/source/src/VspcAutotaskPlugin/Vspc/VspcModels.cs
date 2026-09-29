namespace VspcAutotaskPlugin.Vspc;

public sealed class VspcEnvelope<T>
{
    public VspcMeta? Meta { get; set; }
    public T? Data { get; set; }
    public List<VspcError>? Errors { get; set; }
}

public sealed class VspcMeta { public VspcPagingInfo? PagingInfo { get; set; } }
public sealed class VspcPagingInfo { public long Total { get; set; } public long Count { get; set; } public long Offset { get; set; } }
public sealed class VspcError { public string? Message { get; set; } public int? RetryAfter { get; set; } }

public sealed class VspcCompany
{
    public string InstanceUid { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Status { get; set; }
    public string? ResellerUid { get; set; }
    public string? SubscriptionPlanUid { get; set; }
}

public sealed class VspcUser
{
    public string InstanceUid { get; set; } = "";
    public string? UserName { get; set; }
    public string? Role { get; set; }
    public string? OrganizationUid { get; set; }
}

public sealed class VspcAlarmKnowledge
{
    public string? Summary { get; set; }
    public string? Cause { get; set; }
    public string? Resolution { get; set; }
}

public sealed class VspcAlarmTemplate
{
    public string InstanceUid { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public long? InternalId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public VspcAlarmKnowledge? Knowledge { get; set; }
}

public sealed class VspcAlarmObject
{
    public string? InstanceUid { get; set; }
    public string? Type { get; set; }
    public string? OrganizationUid { get; set; }
    public string? LocationUid { get; set; }
    public string? ManagementAgentUid { get; set; }
    public string? ComputerName { get; set; }
    public string? ObjectUid { get; set; }
    public string? ObjectName { get; set; }
}

public sealed class VspcAlarmActivation
{
    public string? InstanceUid { get; set; }
    public DateTimeOffset? Time { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
    public string? Remark { get; set; }
}

public sealed class VspcActiveAlarm
{
    public string InstanceUid { get; set; } = "";
    public string? AlarmTemplateUid { get; set; }
    public int RepeatCount { get; set; }
    public VspcAlarmObject? Object { get; set; }
    public VspcAlarmActivation? LastActivation { get; set; }
}

public sealed class VspcUsageCounter
{
    public string? Type { get; set; }
    public long Value { get; set; }
}

public sealed class VspcCompanyUsage
{
    public string? CompanyUid { get; set; }
    public string? LocationUid { get; set; }
    public DateTimeOffset? Date { get; set; }
    public List<VspcUsageCounter>? Counters { get; set; }
}

public sealed class VspcSubscriptionPlan
{
    public string InstanceUid { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Type { get; set; }
    public string? Currency { get; set; }
}

public sealed class VspcApiException : Exception
{
    public int StatusCode { get; }
    public VspcApiException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}
