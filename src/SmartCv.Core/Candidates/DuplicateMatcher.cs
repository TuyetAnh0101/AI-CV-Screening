namespace SmartCv.Core.Candidates;

/// <summary>Ứng viên đã có trong hệ thống (chỉ các trường cần để so trùng).</summary>
public sealed record ExistingCandidate(int CandidateId, string FullName, string? Email, string? PhoneNumber);

/// <summary>Một ứng viên có thể là cùng người. MatchedOn: "Email", "Phone" hoặc cả hai.</summary>
public sealed record DuplicateMatch(int CandidateId, string FullName, string? Email, string? PhoneNumber, IReadOnlyList<string> MatchedOn);

/// <summary>
/// A24: nhận ra ứng viên nộp lại. Hàm thuần → unit test được.
/// So trên dữ liệu ĐÃ chuẩn hóa (email chữ thường, điện thoại 10 số) nên "An@Mail.com" và "an@mail.com",
/// "+84 912 345 678" và "0912345678" được coi là trùng. KHÔNG so theo tên: trùng tên rất phổ biến ở Việt Nam.
/// Chỉ GỢI Ý – HR quyết định có gộp hay không.
/// </summary>
public static class DuplicateMatcher
{
    public const string Email = "Email";
    public const string Phone = "Phone";

    public static List<DuplicateMatch> Find(CandidateInput input, IEnumerable<ExistingCandidate> existing, int? excludeCandidateId = null) =>
        Find(input.Email, input.PhoneNumber, existing, excludeCandidateId);

    public static List<DuplicateMatch> Find(string? email, string? phone, IEnumerable<ExistingCandidate> existing, int? excludeCandidateId = null)
    {
        var e = CandidateRules.NormalizeEmail(email);
        var p = CandidateRules.NormalizePhone(phone);
        var result = new List<DuplicateMatch>();
        if (e is null && p is null) return result;

        foreach (var c in existing)
        {
            if (c.CandidateId == excludeCandidateId) continue;
            var on = new List<string>();
            if (e is not null && CandidateRules.NormalizeEmail(c.Email) == e) on.Add(Email);
            if (p is not null && CandidateRules.NormalizePhone(c.PhoneNumber) == p) on.Add(Phone);
            if (on.Count > 0) result.Add(new DuplicateMatch(c.CandidateId, c.FullName, c.Email, c.PhoneNumber, on));
        }

        // trùng cả email và điện thoại lên đầu, rồi ứng viên tạo trước
        return result.OrderByDescending(m => m.MatchedOn.Count).ThenBy(m => m.CandidateId).ToList();
    }
}
