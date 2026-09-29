using System.Text.Json;
using VspcAutotaskPlugin.Autotask;
using Xunit;

namespace VspcAutotaskPlugin.Tests;

public class AtQueryTests
{
    private static JsonElement Serialize(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    [Fact]
    public void Body_wraps_single_clause_in_filter_array()
    {
        var body = Serialize(AtQuery.Body(AtQuery.Eq("isActive", true)));
        Assert.Equal(500, body.GetProperty("MaxRecords").GetInt32());
        var filter = body.GetProperty("filter");
        Assert.Equal(JsonValueKind.Array, filter.ValueKind);
        Assert.Equal("eq", filter[0].GetProperty("op").GetString());
        Assert.Equal("isActive", filter[0].GetProperty("field").GetString());
        Assert.True(filter[0].GetProperty("value").GetBoolean());
    }

    [Fact]
    public void IncludeFields_always_contains_id_for_paging()
    {
        var body = Serialize(AtQuery.Body(AtQuery.Eq("isActive", true),
            includeFields: new[] { "companyName" }));
        var fields = body.GetProperty("IncludeFields").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("id", fields);
        Assert.Contains("companyName", fields);
    }

    [Fact]
    public void And_grouping_nests_items()
    {
        var body = Serialize(AtQuery.Body(AtQuery.And(
            AtQuery.Eq("companyID", 7),
            AtQuery.Gte("startDate", "2026-07-01T00:00:00"))));
        var group = body.GetProperty("filter")[0];
        Assert.Equal("and", group.GetProperty("op").GetString());
        Assert.Equal(2, group.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void In_clause_serializes_value_array()
    {
        var body = Serialize(AtQuery.Body(AtQuery.In("id", new object[] { 1L, 2L, 3L })));
        var clause = body.GetProperty("filter")[0];
        Assert.Equal("in", clause.GetProperty("op").GetString());
        Assert.Equal(3, clause.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public void MaxRecords_is_clamped_to_500()
    {
        var body = Serialize(AtQuery.Body(AtQuery.Eq("id", 1), maxRecords: 9999));
        Assert.Equal(500, body.GetProperty("MaxRecords").GetInt32());
    }
}
