namespace SmartCv.Core.Email;

public sealed record EmailMessage(string ToAddress, string Subject, string Html, string Text);

// ActionPath là đường dẫn tương đối bắt đầu bằng / (ví dụ /verify-email?token=...).
// Token do A56 tạo, lưu hash riêng trong UserTokens. Không truyền URL bên ngoài.
public sealed record EmailTemplateModel(
    string ToAddress,
    string FullName,
    string? ActionPath = null,
    string? Provider = null);
