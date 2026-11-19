using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SmartCv.Core.Entities;

namespace SmartCv.Api.Auth;

public sealed class LoginDummyHash(IOptions<PasswordHasherOptions> options)
{
    // Cùng cấu hình PasswordHasher với tài khoản thật; không phải mật khẩu hay tài khoản mặc định.
    public string Value { get; } = new PasswordHasher<User>(options).HashPassword(new User(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
}
