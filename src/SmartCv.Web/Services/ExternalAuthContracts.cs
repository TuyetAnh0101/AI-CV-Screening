using SmartCv.Core.Contracts;

namespace SmartCv.Web.Services;

/// <summary>Web chỉ tham chiếu Core: không tham chiếu assembly SmartCv.Api.</summary>
public sealed record ExternalExchangeDto(string Token, DateTime ExpiresAt, MeDto User, bool IsNewWorkspace, string ReturnUrl);
public sealed record ExternalLinkStartDto(string StartUrl);

public interface IExternalAuthApi
{
    Task<ApiResult<ExternalExchangeDto>> ExchangeExternalCodeAsync(string code, CancellationToken ct = default);
}
