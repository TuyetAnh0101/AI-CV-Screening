using System.Net;
using Microsoft.Extensions.Configuration;
using SmartCv.Core.Email;

namespace SmartCv.Infrastructure.Email;

public sealed class EmailTemplates(IConfiguration configuration)
{
    public EmailMessage Render(EmailTemplate template, EmailTemplateModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var to = model.ToAddress.Trim();
        if (to.Length is < 3 or > 320 || to.Contains('\r') || to.Contains('\n') || !to.Contains('@'))
            throw new ArgumentException("Địa chỉ người nhận không hợp lệ.", nameof(model));
        if (model.FullName.Length > 150) throw new ArgumentException("Tên người nhận quá dài.", nameof(model));
        var name = string.IsNullOrWhiteSpace(model.FullName) ? "bạn" : model.FullName.Trim();
        var cleanName = WebUtility.HtmlEncode(name);
        var provider = model.Provider ?? "nhà cung cấp đăng nhập";
        if (provider.Length > 100) throw new ArgumentException("Provider quá dài.", nameof(model));
        var cleanProvider = WebUtility.HtmlEncode(provider);
        var needsAction = template is EmailTemplate.VerifyEmail or EmailTemplate.ResetPassword;
        var url = needsAction ? ActionUrl(model.ActionPath) : null;
        var action = url is null ? "" : $"<p><a href=\"{WebUtility.HtmlEncode(url)}\">Mở liên kết bảo mật</a></p>";
        var plainAction = url is null ? "" : $"\nLiên kết: {url}\n";

        string subject;
        string htmlBody;
        string textBody;
        switch (template)
        {
            case EmailTemplate.VerifyEmail:
                subject = "SCApp - Xác minh địa chỉ email";
                htmlBody = $"<p>Xin chào {cleanName},</p><p>Vui lòng xác minh địa chỉ email của bạn. Liên kết có thời hạn 24 giờ.</p>{action}";
                textBody = $"Xin chào {name},\nVui lòng xác minh email. Liên kết có thời hạn 24 giờ.{plainAction}";
                break;
            case EmailTemplate.AccountAlreadyExists:
                subject = "SCApp - Thông báo tài khoản đã tồn tại";
                htmlBody = $"<p>Xin chào {cleanName},</p><p>Một yêu cầu tạo tài khoản đã sử dụng địa chỉ email này. Nếu không phải bạn, bạn có thể bỏ qua thông báo.</p>";
                textBody = $"Xin chào {name},\nĐã có yêu cầu tạo tài khoản sử dụng địa chỉ email này. Nếu không phải bạn, hãy bỏ qua.";
                break;
            case EmailTemplate.RegisteredWithProvider:
                subject = "SCApp - Đăng ký bằng tài khoản liên kết";
                htmlBody = $"<p>Xin chào {cleanName},</p><p>Tài khoản SCApp đã được tạo thông qua {cleanProvider}.</p>";
                textBody = $"Xin chào {name},\nTài khoản SCApp đã được tạo thông qua {provider}.";
                break;
            case EmailTemplate.ResetPassword:
                subject = "SCApp - Đặt lại mật khẩu";
                var providerHelpHtml = model.Provider is not null
                    ? "<p>Tài khoản đăng nhập qua nhà cung cấp bên ngoài cũng có thể đặt mật khẩu SCApp bằng liên kết này.</p>"
                    : "";
                var providerHelpText = model.Provider is not null
                    ? "\nTài khoản đăng nhập qua nhà cung cấp bên ngoài cũng có thể đặt mật khẩu SCApp bằng liên kết này."
                    : "";
                htmlBody = $"<p>Xin chào {cleanName},</p><p>Nếu bạn đã yêu cầu đặt lại mật khẩu, dùng liên kết dưới đây trong vòng 30 phút.</p>{providerHelpHtml}{action}<p>Nếu không yêu cầu, hãy bỏ qua email này.</p>";
                textBody = $"Xin chào {name},\nNếu bạn yêu cầu đặt lại mật khẩu, dùng liên kết trong 30 phút.{providerHelpText}{plainAction}\nNếu không yêu cầu, hãy bỏ qua.";
                break;
            case EmailTemplate.PasswordChanged:
                subject = "SCApp - Mật khẩu đã được thay đổi";
                htmlBody = $"<p>Xin chào {cleanName},</p><p>Mật khẩu SCApp của bạn đã được thay đổi. Nếu không phải bạn, hãy liên hệ quản trị viên.</p>";
                textBody = $"Xin chào {name},\nMật khẩu SCApp của bạn đã được thay đổi. Nếu không phải bạn, hãy liên hệ quản trị viên.";
                break;
            case EmailTemplate.ExternalLoginLinked:
                subject = "SCApp - Đã liên kết phương thức đăng nhập";
                htmlBody = $"<p>Xin chào {cleanName},</p><p>Phương thức đăng nhập {cleanProvider} đã được liên kết với tài khoản của bạn.</p>";
                textBody = $"Xin chào {name},\nPhương thức đăng nhập {provider} đã được liên kết với tài khoản của bạn.";
                break;
            case EmailTemplate.ExternalLoginUnlinked:
                subject = "SCApp - Đã hủy liên kết Google";
                htmlBody = $"<p>Xin chào {cleanName},</p><p>Phương thức đăng nhập {cleanProvider} đã bị hủy liên kết khỏi tài khoản SCApp. Nếu không phải bạn, hãy liên hệ quản trị viên.</p>";
                textBody = $"Xin chào {name},\nPhương thức đăng nhập {provider} đã bị hủy liên kết khỏi SCApp. Nếu không phải bạn, hãy liên hệ quản trị viên.";
                break;
            case EmailTemplate.AdminDiagnostic:
                subject = "SCApp - Kiểm tra hạ tầng email";
                htmlBody = $"<p>Xin chào {cleanName},</p><p>Đây là email kiểm tra do quản trị viên SCApp yêu cầu.</p>";
                textBody = $"Xin chào {name},\nĐây là email kiểm tra do quản trị viên SCApp yêu cầu.";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(template));
        }
        return new EmailMessage(to, subject,
            $"<!doctype html><html lang=\"vi\"><body>{htmlBody}<p>SCApp - Thông báo tự động.</p></body></html>",
            textBody + "\nSCApp - Thông báo tự động.");
    }

    private string ActionUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\')
            || path.Any(char.IsControl) || !Uri.TryCreate(path, UriKind.Relative, out _))
            throw new ArgumentException("ActionPath phải là đường dẫn tương đối an toàn bắt đầu bằng /.", nameof(path));
        var baseUrl = configuration["App:WebBaseUrl"];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) ||
            (root.Scheme != Uri.UriSchemeHttps && !(root.IsLoopback && root.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(root.UserInfo) || !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment))
            throw new InvalidOperationException("Cấu hình App:WebBaseUrl không hợp lệ (yêu cầu HTTPS, ngoại trừ localhost).");
        return new Uri(root, path).AbsoluteUri;
    }
}
