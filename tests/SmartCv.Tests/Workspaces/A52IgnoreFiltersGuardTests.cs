using System.Text.RegularExpressions;

namespace SmartCv.Tests;

/// <summary>
/// A52: mọi chỗ bỏ qua lớp lọc workspace phải nằm trong danh sách cho phép, có lý do và bối cảnh bảo mật.
/// Lời gọi mới không có trong danh sách làm test đỏ; không có mục "cho phép cả thư mục". Hai cách bỏ lọc được kiểm soát:
/// <c>IgnoreQueryFilters()</c> của EF và <c>WorkspaceScope.BeginSystem(reason)</c>.
/// </summary>
public class A52IgnoreFiltersGuardTests
{
    internal sealed record Allowed(string File, string Identifier, string Reason, string SecurityContext);

    /// <summary>Hiện chưa có lời gọi IgnoreQueryFilters() nào trong src/. A53 (Admin: /api/exports, audit) thêm mục tại đây kèm lý do.</summary>
    internal static readonly Allowed[] IgnoreQueryFiltersAllowlist =
    [new("src/SmartCv.Api/Endpoints/ExportEndpoints.cs", "Admin export log list",
        "Admin must see export log rows and Job titles across all workspaces.",
        "Only ListAsync, protected by Admin policy; WorkspaceIsolationAdminTests proves system scope and HR denial."),
     new("src/SmartCv.Api/Reports/AdminOperationsReportReader.cs", "Admin operations report aggregates (v1 + v2)",
        "Global counts of AI ledger units, failed runs/evaluations and export logs across all workspaces; projections are counts/sums/ids only.",
        "Called only by /api/admin/reports and /api/admin/reports/v2 (SystemAdmin policy); B57RoleBoundaryTests R2/R4/R8 prove HR/Owner 403 and anonymous 401, B57ReportScopeTests R7 proves no CV/candidate/prompt/e-mail content in JSON/CSV/XLSX.")];

    internal static readonly Allowed[] BeginSystemAllowlist =
    [
        new("src/SmartCv.Api/Background/RunScoringWorker.cs", "RunScoringWorker.find-active-runs",
            "Hàng đợi lượt chấm nằm trong database và chứa lượt của mọi workspace; chỉ liệt kê ID.",
            "Worker nền (không phải request); mỗi lượt sau đó chạy trong WorkspaceScope.Begin(workspace của lượt). A54 hoàn thiện."),
        new("src/SmartCv.Api/Background/CvExtractionWorker.cs", "CvExtractionWorker.requeue-pending",
            "Khi khởi động cần xếp lại CV Pending của mọi workspace; chỉ liệt kê CvId.",
            "Worker nền; mỗi CV sau đó được đọc chữ trong WorkspaceScope.Begin(workspace của CV). A54 hoàn thiện."),
        new("src/SmartCv.Infrastructure/Privacy/RetentionProcessor.cs", "RetentionProcessor.run-once",
            "Quét hạn lưu và ẩn danh hóa CV đến hạn của mọi workspace (quy định lưu trữ dữ liệu cá nhân).",
            "Chỉ Admin (policy Admin, endpoint /api/retention/run) hoặc RetentionWorker (Development); không đọc/trả nội dung CV."),
        new("src/SmartCv.Infrastructure/Workspaces/WorkerWorkspaces.cs", "worker: đọc workspace của lượt chấm",
            "Worker chỉ có RunId; đọc đúng MỘT cột WorkspaceId của đúng MỘT lượt để mở scope của lượt đó.",
            "System scope hẹp, chỉ SELECT, đóng ngay sau khi đọc."),
        new("src/SmartCv.Infrastructure/Workspaces/WorkerWorkspaces.cs", "worker: đọc workspace của CV",
            "Worker chỉ có CvId; đọc đúng MỘT cột WorkspaceId của đúng MỘT CV để mở scope của CV đó.",
            "System scope hẹp, chỉ SELECT, đóng ngay sau khi đọc."),
    ];

    private static string Root => A51SchemaTests.FindRepositoryRoot();

    private static IEnumerable<(string File, string Text)> SourceFiles() =>
        Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => (Path.GetRelativePath(Root, f).Replace('\\', '/'), File.ReadAllText(f)));

    private static string StripComments(string text) => Regex.Replace(text, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

    [Fact]
    public void Every_IgnoreQueryFilters_call_in_src_is_on_the_allowlist()
    {
        var found = SourceFiles().SelectMany(f => Regex.Matches(StripComments(f.Text), @"\bIgnoreQueryFilters\s*[(<]").Select(_ => f.File)).ToList();
        var unapproved = found.Where(file => IgnoreQueryFiltersAllowlist.All(a => a.File != file)).Distinct().ToList();
        Assert.True(unapproved.Count == 0, "IgnoreQueryFilters chưa được phê duyệt tại: " + string.Join(", ", unapproved)
            + ". Thêm vào danh sách cho phép kèm lý do và bối cảnh bảo mật (và test chứng minh không lộ dữ liệu), hoặc bỏ lời gọi.");
        // Mỗi mục trong danh sách phải còn tồn tại (không để mục cũ che lời gọi mới trong cùng file).
        Assert.All(IgnoreQueryFiltersAllowlist, a => Assert.Contains(a.File, found));
    }

    [Fact]
    public void Every_BeginSystem_call_in_src_is_on_the_allowlist_with_an_exact_reason()
    {
        var calls = SourceFiles().SelectMany(f => Regex.Matches(StripComments(f.Text), @"WorkspaceScope\s*\.\s*BeginSystem\s*\(\s*""([^""]+)""")
            .Select(m => (f.File, Reason: m.Groups[1].Value))).ToList();
        var unapproved = calls.Where(c => BeginSystemAllowlist.All(a => a.File != c.File || a.Identifier != c.Reason)).ToList();
        Assert.True(unapproved.Count == 0, "BeginSystem chưa được phê duyệt: " + string.Join("; ", unapproved.Select(u => $"{u.File} [{u.Reason}]")));
        // Lời gọi BeginSystem phải có lý do là chuỗi hằng (không dùng biến) để guard kiểm tra được.
        var all = SourceFiles().SelectMany(f => Regex.Matches(StripComments(f.Text), @"WorkspaceScope\s*\.\s*BeginSystem\s*\((?!\s*"")").Select(_ => f.File)).ToList();
        Assert.True(all.Count == 0, "BeginSystem phải truyền lý do là chuỗi hằng: " + string.Join(", ", all));
        Assert.All(BeginSystemAllowlist, a => Assert.Contains(calls, c => c.File == a.File && c.Reason == a.Identifier));
    }

    [Fact]
    public void Allowlist_entries_are_specific_and_documented()
    {
        foreach (var entry in IgnoreQueryFiltersAllowlist.Concat(BeginSystemAllowlist))
        {
            Assert.EndsWith(".cs", entry.File);                 // một file cụ thể, không cho phép cả thư mục
            Assert.DoesNotContain("*", entry.File);
            Assert.True(File.Exists(Path.Combine(Root, entry.File.Replace('/', Path.DirectorySeparatorChar))), entry.File);
            Assert.False(string.IsNullOrWhiteSpace(entry.Identifier));
            Assert.True(entry.Reason.Length >= 20 && entry.SecurityContext.Length >= 20, $"{entry.File}: cần lý do nghiệp vụ và bối cảnh bảo mật rõ ràng");
        }
        Assert.Equal(BeginSystemAllowlist.Length, BeginSystemAllowlist.Select(a => (a.File, a.Identifier)).Distinct().Count());
    }

    [Fact]
    public void Request_code_never_opens_a_scope_from_client_input()
    {
        // Endpoint/Middleware/Controller (đường request) không được mở WorkspaceScope.Begin: workspace của request chỉ đến từ validator.
        var offenders = SourceFiles()
            .Where(f => f.File.StartsWith("src/SmartCv.Api/Endpoints/") || f.File.StartsWith("src/SmartCv.Api/Controllers/") || f.File.StartsWith("src/SmartCv.Api/Middleware/"))
            .Where(f => Regex.IsMatch(StripComments(f.Text), @"WorkspaceScope\s*\.\s*Begin(System)?\s*\("))
            .Select(f => f.File).ToList();
        Assert.Empty(offenders);
    }
}
