using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Api.Endpoints;
using SmartCv.Core.Candidates;
using SmartCv.Infrastructure.Candidates;

namespace SmartCv.Tests.Candidates;

public class CandidateSafetyTests
{
    [Theory]
    [InlineData("0١٢٣٤٥٦٧٨٩")]
    [InlineData("０９１２３４５６７８")]
    [InlineData("0912345678x")]
    [InlineData("+1 912345678")]
    public void Phone_must_match_the_SQL_ASCII_digit_constraint(string value) => Assert.Null(CandidateRules.NormalizePhone(value));

    [Fact]
    public void Boundary_lengths_and_whitespace_are_validated_before_persistence()
    {
        Assert.NotNull(CandidateRules.Validate(new string('a', 150), "a@b.com", null).Input);
        Assert.Null(CandidateRules.Validate(new string('a', 151), "a@b.com", null).Input);
        var maximum = new string('a', 64) + "@" + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 58) + ".vn";
        Assert.Equal(254, maximum.Length);
        Assert.NotNull(CandidateRules.Validate("Test Name", maximum, null).Input);
        Assert.Null(CandidateRules.Validate("Test Name", "x" + maximum, null).Input);
        Assert.Equal("Test Name", CandidateRules.NormalizeName(" Test\t\r\nName "));
        Assert.Null(CandidateRules.NormalizeName(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_text_produces_no_contact_suggestion(string? text)
    {
        var result = CandidateInfoExtractor.Suggest(text);
        Assert.Null(result.FullName); Assert.Null(result.Email); Assert.Null(result.PhoneNumber);
    }

    [Theory]
    [InlineData("timeout", 503)]
    [InlineData("concurrency", 409)]
    [InlineData("unexpected", 500)]
    public async Task Errors_are_safe_have_no_store_and_never_retry_a_write(string error, int status)
    {
        var http = new DefaultHttpContext(); http.Request.Method = "POST";
        var invocation = EndpointFilterInvocationContext.Create(http);
        Exception failure = error switch
        {
            "timeout" => new TimeoutException("PRIVATE email and SQL"),
            "concurrency" => new DbUpdateConcurrencyException("PRIVATE email and SQL"),
            _ => new InvalidOperationException("PRIVATE email and SQL")
        };
        var attempts = 0;
        var filter = new CandidateApiFilter(NullLogger<CandidateApiFilter>.Instance);
        var result = await filter.InvokeAsync(invocation, _ => { attempts++; return ValueTask.FromException<object?>(failure); });
        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(status, problem.ProblemDetails.Status); Assert.Equal(1, attempts);
        Assert.DoesNotContain("PRIVATE", problem.ProblemDetails.Detail);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Cancelled_request_keeps_the_original_token_and_releases_waiting_write_scope()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var http = new DefaultHttpContext { RequestAborted = cts.Token };
        var filter = new CandidateApiFilter(NullLogger<CandidateApiFilter>.Instance);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await filter.InvokeAsync(EndpointFilterInvocationContext.Create(http),
                _ => ValueTask.FromException<object?>(new OperationCanceledException(cts.Token))));
        Assert.Equal(cts.Token, error.CancellationToken);
        await using var db = TestSupport.InMemoryDb(Guid.NewGuid().ToString());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await CandidateWriteScope.BeginAsync(db, cts.Token));
        await using var available = await CandidateWriteScope.BeginAsync(db, CancellationToken.None);
    }

    [Fact]
    public async Task Duplicate_query_does_not_lose_the_best_match_after_twenty_other_contacts()
    {
        await using var db = TestSupport.InMemoryDb(Guid.NewGuid().ToString());
        db.Candidates.AddRange(Enumerable.Range(1, 25).Select(i => new SmartCv.Core.Entities.Candidate
        { FullName = "Synthetic " + i, Email = "same@example.com", PhoneNumber = i == 25 ? "0912345678" : null }));
        await db.SaveChangesAsync();
        var matches = await CandidateQueries.FindDuplicatesAsync(db, "SAME@EXAMPLE.COM", "+84 912 345 678", null, default);
        Assert.Equal(25, matches.Count); Assert.Equal(2, matches[0].MatchedOn.Count);
        Assert.Equal(25, matches[0].CandidateId);
    }
}
