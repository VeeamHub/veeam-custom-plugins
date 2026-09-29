namespace VspcAutotaskPlugin.Autotask;

// Read models only — writes are sent as dictionaries with exact Autotask field
// names to avoid any casing ambiguity (companyID, queueID, ...).

public sealed class AtZoneInfo
{
    public string? ZoneName { get; set; }
    public string? Url { get; set; }
    public string? WebUrl { get; set; }
}

public sealed class AtPageDetails
{
    public int Count { get; set; }
    public int RequestCount { get; set; }
    public string? NextPageUrl { get; set; }
    public string? PrevPageUrl { get; set; }
}

public sealed class AtQueryResponse<T>
{
    public List<T> Items { get; set; } = new();
    public AtPageDetails? PageDetails { get; set; }
}

public sealed class AtSingleResponse<T> { public T? Item { get; set; } }
public sealed class AtItemResponse { public long ItemId { get; set; } }
public sealed class AtCountResponse { public long QueryCount { get; set; } }

public sealed class AtCompany
{
    public long Id { get; set; }
    public string? CompanyName { get; set; }
    public int? CompanyType { get; set; }
    public bool? IsActive { get; set; }
}

public sealed class AtTicket
{
    public long Id { get; set; }
    public string? TicketNumber { get; set; }
    public long CompanyID { get; set; }
    public string? Title { get; set; }
    public int? Status { get; set; }
    public int? Priority { get; set; }
    public long? QueueID { get; set; }
    public DateTime? DueDateTime { get; set; }
    public DateTime? CreateDate { get; set; }
    public DateTime? LastActivityDate { get; set; }
}

public sealed class AtContract
{
    public long Id { get; set; }
    public long CompanyID { get; set; }
    public string? ContractName { get; set; }
    public int? ContractType { get; set; }
    public int? Status { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
}

public sealed class AtService
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public decimal? UnitPrice { get; set; }
    public int? PeriodType { get; set; }
    public long? BillingCodeID { get; set; }
    public bool? IsActive { get; set; }
}

public sealed class AtContractService
{
    public long Id { get; set; }
    public long ContractID { get; set; }
    public long ServiceID { get; set; }
    public decimal? UnitPrice { get; set; }
    public string? InvoiceDescription { get; set; }
}

public sealed class AtContractServiceUnit
{
    public long Id { get; set; }
    public long ContractID { get; set; }
    public long ContractServiceID { get; set; }
    public long ServiceID { get; set; }
    public long Units { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
}

public sealed class AtBillingCode
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public bool? IsActive { get; set; }
    public int? UseType { get; set; }
}

public sealed class AtFieldsResponse { public List<AtFieldInfo>? Fields { get; set; } }

public sealed class AtFieldInfo
{
    public string? Name { get; set; }
    public string? DataType { get; set; }
    public bool IsRequired { get; set; }
    public bool IsPickList { get; set; }
    public bool IsReference { get; set; }
    public string? ReferenceEntityType { get; set; }
    public List<AtPicklistValue>? PicklistValues { get; set; }
}

public sealed class AtPicklistValue
{
    public string? Value { get; set; }
    public string? Label { get; set; }
    public bool IsDefaultValue { get; set; }
    public bool IsActive { get; set; }
    public int? SortOrder { get; set; }
}

public sealed class AtApiException : Exception
{
    public int StatusCode { get; }
    public AtApiException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}
