using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace SmartCv.Tests;

/// <summary>
/// A10 test lifecycle: lỗi khởi động phải đến tay test đúng loại, kể cả khi luồng test bị lập lịch chậm.
/// Trước khi sửa, WebApplicationFactory gốc ném ObjectDisposedException('IServiceProvider') từ
/// DeferredHostBuilder.DeferredHost.StartAsync khi Program đã khởi động lỗi và dispose host trước (race khi chạy song song),
/// làm Weights_not_summing_to_one_stop_startup lúc PASS lúc FAIL. Xem StartupGatedApiFactory trong TestSupport.
/// </summary>
public class TestHostLifecycleTests
{
    /// <summary>Luồng test chậm 500 ms giữa Build và Start: đủ để Program chạy xong và dispose host nếu không có cổng chặn.</summary>
    private sealed class SlowTestThreadFactory : StartupGatedApiFactory
    {
        protected override void BeforeHostStart() => Thread.Sleep(500);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Slow(string aiWeight) =>
        new SlowTestThreadFactory().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:SmartCvDb", "Server=(local);Database=SmartCv_Test_NotUsed;Trusted_Connection=True");
            b.UseSetting("Gemini:ApiKey", "test-not-a-real-key");
            b.UseSetting("Scoring:AiWeight", aiWeight);
        });

    [Fact]
    public void Startup_validation_error_is_reported_even_when_the_test_thread_is_slow()
    {
        using var factory = Slow("0.5");   // 0.3 + 0.5 ≠ 1: ValidateOnStart dừng khởi động

        var ex = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("trọng số", ex.Message);
    }

    [Fact]
    public async Task Valid_startup_with_a_slow_test_thread_still_serves_requests()
    {
        using var factory = Slow("0.7");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health");

        Assert.True(response.IsSuccessStatusCode);
    }
}
