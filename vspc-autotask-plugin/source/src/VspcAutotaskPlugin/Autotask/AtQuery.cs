namespace VspcAutotaskPlugin.Autotask;

/// <summary>Builds Autotask REST query bodies ({"MaxRecords":..,"IncludeFields":..,"filter":[..]}).</summary>
public static class AtQuery
{
    public static Dictionary<string, object?> Eq(string field, object? value) =>
        new() { ["op"] = "eq", ["field"] = field, ["value"] = value };

    public static Dictionary<string, object?> NotEq(string field, object? value) =>
        new() { ["op"] = "noteq", ["field"] = field, ["value"] = value };

    public static Dictionary<string, object?> Gt(string field, object? value) =>
        new() { ["op"] = "gt", ["field"] = field, ["value"] = value };

    public static Dictionary<string, object?> Gte(string field, object? value) =>
        new() { ["op"] = "gte", ["field"] = field, ["value"] = value };

    public static Dictionary<string, object?> Lte(string field, object? value) =>
        new() { ["op"] = "lte", ["field"] = field, ["value"] = value };

    public static Dictionary<string, object?> Contains(string field, string value) =>
        new() { ["op"] = "contains", ["field"] = field, ["value"] = value };

    public static Dictionary<string, object?> In(string field, IEnumerable<object> values) =>
        new() { ["op"] = "in", ["field"] = field, ["value"] = values.ToArray() };

    public static Dictionary<string, object?> And(params object[] items) =>
        new() { ["op"] = "and", ["items"] = items };

    public static Dictionary<string, object?> Or(params object[] items) =>
        new() { ["op"] = "or", ["items"] = items };

    /// <summary>Wraps one or more filter clauses into a full query body.</summary>
    public static Dictionary<string, object?> Body(object filter, int maxRecords = 500, string[]? includeFields = null)
    {
        var filterArray = filter as object[] ?? new[] { filter };
        var body = new Dictionary<string, object?>
        {
            ["MaxRecords"] = Math.Clamp(maxRecords, 1, 500),
            ["filter"] = filterArray
        };
        if (includeFields is { Length: > 0 })
        {
            // Autotask requires id in IncludeFields when paging beyond 500 rows.
            body["IncludeFields"] = includeFields.Contains("id") ? includeFields : includeFields.Append("id").ToArray();
        }
        return body;
    }
}
