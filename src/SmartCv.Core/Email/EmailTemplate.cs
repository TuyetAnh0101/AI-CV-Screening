namespace SmartCv.Core.Email;

// Các template giao dịch. AdminDiagnostic chỉ phục vụ endpoint Admin A55.
public enum EmailTemplate
{
    VerifyEmail,
    AccountAlreadyExists,
    RegisteredWithProvider,
    ResetPassword,
    PasswordChanged,
    ExternalLoginLinked,
    AdminDiagnostic
}
