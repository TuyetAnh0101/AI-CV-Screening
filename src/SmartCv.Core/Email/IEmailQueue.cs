namespace SmartCv.Core.Email;

/// <summary>Chỉ Add vào DbContext. Caller phải SaveChanges/Commit trong cùng transaction với nghiệp vụ.</summary>
public interface IEmailQueue
{
    Task EnqueueAsync(int? userId, EmailTemplate template, EmailTemplateModel model,
        CancellationToken cancellationToken = default);
}
