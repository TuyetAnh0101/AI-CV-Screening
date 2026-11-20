extern alias web;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

internal static class B43UiTestSupport
{
    // In-memory verified session for UI fixtures; never reads User Secrets, contacts SQL, or creates a real account.
    internal static AuthSession Session(string name = "HR - Lan", string role = "HR")
    {
        var session = B27.Session();
        session.SignInAsync(new LoginResponse { Token = "synthetic-" + Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddHours(1), User = B27.User(role, name: name) }).GetAwaiter().GetResult();
        return session;
    }
    internal static HrActorState Actor(string name = "HR - Lan") => new(Session(name));
}
