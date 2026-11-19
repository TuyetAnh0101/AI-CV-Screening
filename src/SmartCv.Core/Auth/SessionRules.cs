namespace SmartCv.Core.Auth;

public sealed record UserSessionState(bool IsActive, string SecurityStamp);

public static class SessionRules
{
    public static string? Check(UserSessionState? state, string? tokenStamp)
    {
        if (string.IsNullOrWhiteSpace(tokenStamp)) return "Token thiếu thông tin phiên đăng nhập.";
        if (state is null) return "Tài khoản không còn tồn tại.";
        if (!state.IsActive) return "Tài khoản đã bị khóa.";
        if (string.IsNullOrWhiteSpace(state.SecurityStamp) || !string.Equals(state.SecurityStamp, tokenStamp, StringComparison.Ordinal))
            return "Phiên đăng nhập đã hết hiệu lực.";
        return null;
    }
}
