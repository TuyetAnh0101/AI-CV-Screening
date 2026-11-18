using SmartCv.Core.Cvs;

// A23: root namespace on purpose. "SmartCv.Tests.Cvs" (as in the ZIP) would shadow the B23 page type "Cvs" in every
// SmartCv.Tests file, the same CS0118 trap as SmartCv.Tests.Runs vs the B26 "Runs" page.
namespace SmartCv.Tests;

/// <summary>A23: bộ lọc kho CV (23 test theo hướng dẫn, giữ nguyên nội dung).</summary>
public class CvListQueryTests
{
    [Fact]
    public void MacDinh_Trang1_20Dong()
    {
        var (q, err) = CvListQuery.Create(null, null, null, null, null, null, null);
        Assert.Null(err);
        Assert.Equal(1, q!.Page);
        Assert.Equal(20, q.PageSize);
        Assert.Equal(0, q.Skip);
        Assert.Null(q.Search);
    }

    [Theory]
    [InlineData("  react   native ", "react native")]
    [InlineData("C#", "C#")]
    [InlineData("   ", null)]
    public void ChuanHoaTuKhoa(string input, string? expected) =>
        Assert.Equal(expected, CvListQuery.NormalizeSearch(input));

    [Fact]
    public void TuKhoaQuaNgan_Loi()
    {
        var (q, err) = CvListQuery.Create("a", null, null, null, null, null, null);
        Assert.Null(q);
        Assert.Contains("ít nhất", err);
    }

    [Fact]
    public void TuKhoaQuaDai_Loi()
    {
        var (_, err) = CvListQuery.Create(new string('x', 101), null, null, null, null, null, null);
        Assert.Contains("tối đa", err);
    }

    [Theory]
    [InlineData("Hired")]
    [InlineData("new")]           // phân biệt hoa thường: phải đúng mã "New"
    public void GiaiDoanSai_Loi(string stage)
    {
        var (_, err) = CvListQuery.Create(null, null, stage, null, null, null, null);
        Assert.StartsWith("stage", err);
    }

    [Fact]
    public void NguonSai_Loi() =>
        Assert.StartsWith("source", CvListQuery.Create(null, null, null, "Facebook", null, null, null).Error);

    [Theory]
    [InlineData("Pending")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Anonymized")]
    public void TrangThaiHopLe(string status) =>
        Assert.Null(CvListQuery.Create(null, null, null, null, status, null, null).Error);

    [Fact]
    public void TrangThaiSai_Loi() =>
        Assert.StartsWith("status", CvListQuery.Create(null, null, null, null, "Done", null, null).Error);

    [Theory]
    [InlineData(0, null)]
    [InlineData(null, 0)]
    [InlineData(null, 101)]
    public void PhanTrangSai_Loi(int? page, int? size) =>
        Assert.NotNull(CvListQuery.Create(null, null, null, null, null, page, size).Error);

    [Fact]
    public void Trang3_Co10Dong_Bo20()
    {
        var (q, _) = CvListQuery.Create("java", 2, "Rejected", "Email", "Completed", 3, 10);
        Assert.Equal(20, q!.Skip);
        Assert.Equal(2, q.JobId);
        Assert.Equal("Rejected", q.Stage);
        Assert.Equal("Email", q.Source);
    }

    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(1, 20, 1)]
    [InlineData(20, 20, 1)]
    [InlineData(21, 20, 2)]
    [InlineData(500, 20, 25)]
    public void SoTrang(int total, int size, int pages) => Assert.Equal(pages, CvListQuery.TotalPages(total, size));
}

/// <summary>A23: đọc query string thật (tham số lặp, số nguyên sai, tràn phân trang, giá trị rỗng).</summary>
public class A23CvListQueryParseTests
{
    private static (CvListQuery? Query, Dictionary<string, string[]> Errors) Parse(params (string Key, string Value)[] pairs) =>
        CvListQuery.Parse(pairs.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Select(p => p.Value).ToArray()));

    [Fact]
    public void Empty_values_mean_no_filter_and_unknown_keys_are_ignored()
    {
        var (q, errors) = Parse(("search", " "), ("jobId", ""), ("stage", ""), ("source", ""), ("status", ""), ("page", ""), ("pageSize", ""), ("utm", "x"));
        Assert.Empty(errors);
        Assert.Equal(new CvListQuery(null, null, null, null, null, 1, 20), q);
    }

    [Fact]
    public void Valid_values_are_trimmed_and_kept_exactly()
    {
        var (q, errors) = Parse(("search", "  SQL   Server "), ("jobId", "2"), ("stage", " Rejected "), ("source", "Email"), ("status", "Anonymized"), ("page", "3"), ("pageSize", "100"));
        Assert.Empty(errors);
        Assert.Equal(new CvListQuery("SQL Server", 2, "Rejected", "Email", "Anonymized", 3, 100), q);
        Assert.Equal(200, q!.Skip);
    }

    [Theory]
    [InlineData("jobId", "abc")]
    [InlineData("jobId", "1.5")]
    [InlineData("jobId", "2147483648")]
    [InlineData("page", "x")]
    [InlineData("pageSize", "20 ")]
    [InlineData("jobId", "0")]
    [InlineData("jobId", "-3")]
    [InlineData("page", "-1")]
    [InlineData("pageSize", "101")]
    [InlineData("search", "a")]
    [InlineData("stage", "Hired")]
    [InlineData("source", "email")]
    [InlineData("status", "anonymized")]
    public void Invalid_value_reports_that_field(string key, string value)
    {
        var (q, errors) = Parse((key, value));
        Assert.Null(q);
        Assert.Equal(key, Assert.Single(errors).Key);
    }

    [Fact]
    public void Repeated_parameter_is_rejected_even_with_different_casing()
    {
        var parameters = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["page"] = ["1"], ["PAGE"] = ["2"] };
        var (q, errors) = CvListQuery.Parse(parameters);
        Assert.Null(q);
        Assert.Contains("page", errors.Keys);
        Assert.Null(Parse(("search", "react"), ("search", "java")).Query);
    }

    [Theory]
    [InlineData("107374183", "20", true)]    // (page − 1) × 20 = 2 147 483 640 ≤ int.MaxValue
    [InlineData("107374184", "20", false)]
    [InlineData("2147483647", "1", true)]
    [InlineData("2147483647", "100", false)]
    public void Page_offset_never_overflows(string page, string size, bool valid)
    {
        var (q, errors) = Parse(("page", page), ("pageSize", size));
        Assert.Equal(valid, q is not null);
        if (valid) Assert.True(q!.Skip >= 0); else Assert.Contains("page", errors.Keys);
    }

    [Fact]
    public void Search_length_is_measured_after_whitespace_is_collapsed()
    {
        Assert.Null(Parse(("search", "a     ")).Query);
        Assert.NotNull(Parse(("search", "a b")).Query);
        Assert.NotNull(Parse(("search", new string('x', 100))).Query);
        Assert.Null(Parse(("search", new string('x', 101))).Query);
        Assert.Equal("50% _x [y]", Parse(("search", " 50%   _x  [y] ")).Query!.Search); // wildcards are data, escaped by EF
    }
}
