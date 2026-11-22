using Microsoft.AspNetCore.Routing;

namespace SmartCv.Api.Auditing;

public sealed record AuditAction(string Code, string Category, string ResourceType, string Summary, string? RouteKey = "id", bool Required = false);

/// <summary>Explicit allowlist of real routes. No URL, body, query, or arbitrary metadata is persisted.</summary>
public static class AuditCatalog
{
    public static IReadOnlyDictionary<string, AuditAction> Routes { get; } = Build();
    private static Dictionary<string, AuditAction> Build()
    {
        var entries = new Dictionary<string, AuditAction>(StringComparer.OrdinalIgnoreCase);
        void Add(string route, string code, string category, string resource, string summary, string? key = "id", bool required = false) =>
            entries.Add(route, new(code, category, resource, summary, key, required));
        Add("POST /api/auth/login", "auth.login", "Authentication", "Session", "Đăng nhập", null);
        Add("POST /api/auth/register", "auth.register", "Authentication", "User", "Yêu cầu đăng ký tài khoản", null);
        Add("POST /api/auth/verify-email", "auth.verify_email", "Authentication", "User", "Xác minh email tài khoản", null);
        Add("POST /api/auth/resend-verification", "auth.resend_verification", "Authentication", "User", "Yêu cầu gửi lại xác minh", null);
        Add("POST /api/admin/email/test", "email.test_requested", "Email", "EmailOutbox", "Gửi email kiểm tra", null);
        Add("POST /api/auth/change-password", "auth.password_changed", "Authentication", "User", "Đổi mật khẩu cá nhân", null);
        Add("DELETE /api/auth/me/logins/{provider}", "auth.external_unlink", "Authentication", "User", "Hủy liên kết phương thức đăng nhập", null);
        Add("POST /api/auth/forgot-password", "auth.forgot_password", "Authentication", "User", "Yêu cầu khôi phục mật khẩu", null);
        Add("POST /api/auth/reset-password", "auth.reset_password", "Authentication", "User", "Đặt lại mật khẩu bằng liên kết", null);
        Add("PUT /api/auth/me/profile", "account.profile_updated", "Accounts", "User", "Cập nhật họ tên cá nhân", null);
        Add("POST /api/auth/me/password-setup", "auth.password_setup", "Authentication", "User", "Yêu cầu đặt mật khẩu lần đầu", null);
        Add("POST /api/users", "accounts.created", "Accounts", "User", "Tạo tài khoản", null);
        Add("PUT /api/users/{id:int}", "accounts.updated", "Accounts", "User", "Cập nhật tài khoản");
        Add("POST /api/users/{id:int}/lock", "accounts.locked", "Accounts", "User", "Khóa tài khoản");
        Add("POST /api/users/{id:int}/unlock", "accounts.unlocked", "Accounts", "User", "Mở khóa tài khoản");
        Add("POST /api/users/{id:int}/reset-password", "accounts.password_reset", "Accounts", "User", "Đặt lại mật khẩu HR");
        Add("GET /api/cvs/{id:guid}/file", "cvs.file_access_granted", "Cvs", "Cv", "Cấp quyền đọc file CV và mở stream", required: true);
        Add("POST /api/cvs/upload", "cvs.upload", "Cvs", "Cv", "Xử lý yêu cầu tải CV lên", null);
        Add("POST /api/cvs/{id:guid}/extract", "cvs.extraction_requested", "Cvs", "Cv", "Yêu cầu đọc lại chữ CV");
        Add("PUT /api/cvs/{id:guid}/candidate", "cvs.candidate_attached", "Candidates", "Cv", "Gắn CV vào ứng viên");
        Add("POST /api/candidates", "candidates.created", "Candidates", "Candidate", "Tạo ứng viên", null);
        Add("PUT /api/candidates/{id:int}", "candidates.updated", "Candidates", "Candidate", "Cập nhật ứng viên");
        Add("POST /api/candidates/{id:int}/merge", "candidates.merged", "Candidates", "Candidate", "Gộp ứng viên");
        Add("POST /api/candidates/{id:int}/erase", "candidates.erased", "Privacy", "Candidate", "Xóa dữ liệu theo yêu cầu ứng viên");
        Add("GET /api/candidates/{id:int}/data-export", "candidates.export_access_granted", "Privacy", "Candidate", "Chuẩn bị và cấp quyền xuất dữ liệu ứng viên");
        Add("POST /api/applications", "applications.assigned", "Recruitment", "Application", "Xử lý yêu cầu gán hồ sơ vào vị trí", null);
        Add("DELETE /api/applications/{id:int}", "applications.removed", "Recruitment", "Application", "Bỏ hồ sơ khỏi vị trí");
        Add("PATCH /api/applications/{id:int}/stage", "applications.stage_changed", "Recruitment", "Application", "Đổi giai đoạn hồ sơ");
        Add("POST /api/applications/stage", "applications.stages_changed", "Recruitment", "Application", "Đổi giai đoạn nhiều hồ sơ", null);
        Add("POST /api/applications/{id:int}/flags", "applications.flag_created", "Recruitment", "Application", "Gắn cờ kiểm tra hồ sơ");
        Add("POST /api/applications/flags/{flagId:int}/resolve", "applications.flag_resolved", "Recruitment", "ApplicationFlag", "Đánh dấu cờ đã xử lý", "flagId");
        Add("POST /api/applications/flags/{flagId:int}/reopen", "applications.flag_reopened", "Recruitment", "ApplicationFlag", "Mở lại cờ kiểm tra", "flagId");
        Add("POST /api/jobs", "jobs.created", "Recruitment", "Job", "Tạo vị trí tuyển dụng", null);
        Add("PUT /api/jobs/{id:int}", "jobs.updated", "Recruitment", "Job", "Cập nhật vị trí tuyển dụng");
        Add("POST /api/jobs/{id:int}/activate", "jobs.activated", "Recruitment", "Job", "Mở tuyển vị trí");
        Add("POST /api/jobs/{id:int}/deactivate", "jobs.deactivated", "Recruitment", "Job", "Ngừng tuyển vị trí");
        Add("POST /api/jobs/{id:int}/skills", "jobs.skill_added", "Recruitment", "Job", "Thêm kỹ năng vị trí");
        Add("PUT /api/jobs/{id:int}/skills/{code}", "jobs.skill_updated", "Recruitment", "Job", "Cập nhật kỹ năng vị trí");
        Add("DELETE /api/jobs/{id:int}/skills/{code}", "jobs.skill_removed", "Recruitment", "Job", "Bỏ kỹ năng vị trí");
        Add("PUT /api/jobs/{id:int}/skills/{code}/aliases", "jobs.aliases_updated", "Recruitment", "Job", "Cập nhật từ khóa kỹ năng");
        Add("POST /api/runs", "runs.started", "Ai", "EvaluationRun", "Khởi tạo lượt chấm CV", null);
        Add("GET /api/runs/{id:int}/ranking/export", "runs.ranking_exported", "Recruitment", "EvaluationRun", "Chuẩn bị và cấp quyền xuất bảng xếp hạng", required: true);
        Add("POST /api/prompts", "prompts.created", "Ai", "PromptVersion", "Tạo phiên bản System Prompt", null);
        Add("POST /api/prompts/{id:int}/activate", "prompts.activated", "Ai", "PromptVersion", "Kích hoạt phiên bản System Prompt");
        Add("POST /api/prompts/assembled-preview", "prompts.previewed", "Ai", "PromptVersion", "Xem trước nội dung ghép prompt", null);
        Add("POST /api/applications/{id:int}/ai-preview", "ai.preview_requested", "Ai", "Application", "Yêu cầu chấm thử AI");
        Add("POST /api/prompt-tests", "prompt_tests.started", "Ai", "EvaluationRun", "Khởi tạo đánh giá prompt", null);
        Add("POST /api/evaluation-sets", "evaluation_sets.created", "EvaluationSets", "EvaluationSet", "Tạo bộ dữ liệu đánh giá", null);
        Add("POST /api/evaluation-sets/{id:int}/members", "evaluation_sets.members_added", "EvaluationSets", "EvaluationSet", "Thêm hồ sơ vào bộ đánh giá");
        Add("DELETE /api/evaluation-sets/{id:int}/members/{applicationId:int}", "evaluation_sets.member_removed", "EvaluationSets", "EvaluationSet", "Bỏ hồ sơ khỏi bộ đánh giá");
        Add("PUT /api/evaluation-sets/{id:int}/labels", "evaluation_sets.labels_saved", "EvaluationSets", "EvaluationSet", "Lưu nhãn HR");
        Add("POST /api/evaluation-sets/{id:int}/labels/import", "evaluation_sets.labels_imported", "EvaluationSets", "EvaluationSet", "Nhập nhãn HR từ CSV");
        Add("DELETE /api/evaluation-sets/{id:int}/labels/{applicationId:int}", "evaluation_sets.label_removed", "EvaluationSets", "EvaluationSet", "Bỏ nhãn HR");
        Add("POST /api/retention/run", "privacy.retention_requested", "Privacy", "Retention", "Yêu cầu quét hạn lưu CV", null);
        Add("POST /api/cvs/{id:guid}/anonymize", "cvs.anonymized", "Privacy", "Cv", "Ẩn danh hóa CV");
        Add("POST /api/workspace/sample-job", "workspace.sample_job",
             "Workspace", "Job", "Tạo vị trí mẫu cho không gian cá nhân", null);
        return entries;
    }
    public static AuditAction? For(HttpContext http)
    {
        var path = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        return path is null ? null : Routes.GetValueOrDefault(http.Request.Method + " /" + path.Trim('/'));
    }
    public static AuditAction ByCode(string code) => code switch
    {
        "accounts.role_changed" => new(code, "Accounts", "User", "Thay đổi vai trò tài khoản"),
        "auth.external_login" => new(code, "Authentication", "User", "Đăng nhập bằng Google"),
        "auth.external_register" => new(code, "Authentication", "User", "Tạo tài khoản bằng Google"),
        "auth.external_link" => new(code, "Authentication", "User", "Liên kết Google"),
        "auth.external_rejected" => new(code, "Authentication", "User", "Từ chối đăng nhập hoặc liên kết ngoài"),
        "auth.session_rejected" => new(code, "Authentication", "Session", "Từ chối phiên truy cập không hợp lệ", null),
        "workspace.provisioned" => new(code, "Workspace", "Workspace", "Khởi tạo không gian cá nhân"),
        "ai.quota_exceeded" => new(code, "Ai", "Workspace", "Từ chối yêu cầu do vượt hạn mức AI"),
        _ => Routes.Values.FirstOrDefault(a => a.Code == code) ?? throw new ArgumentException("Unknown audit action.", nameof(code))
    };
}
