using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Candidates;
using SmartCv.Core.Contracts;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Candidates;

/// <summary>A24: truy vấn dùng chung cho API ứng viên và API gắn CV.</summary>
public static class CandidateQueries
{
    /// <summary>
    /// Ứng viên có cùng email HOẶC cùng số điện thoại (đã chuẩn hóa). Database so email không phân biệt hoa thường
    /// (collation mặc định), rồi DuplicateMatcher so lại trên C# để biết khớp theo trường nào.
    /// </summary>
    public static async Task<List<DuplicateMatchDto>> FindDuplicatesAsync(
        SmartCvDbContext db, string? email, string? phone, int? excludeCandidateId, CancellationToken ct)
    {
        var e = CandidateRules.NormalizeEmail(email);
        var p = CandidateRules.NormalizePhone(phone);
        if (e is null && p is null) return [];

        var rows = await db.Candidates.AsNoTracking()
            .Where(c => c.CandidateId != excludeCandidateId &&
                ((e != null && c.Email != null && c.Email.ToLower() == e) || (p != null && c.PhoneNumber == p)))
            .OrderByDescending(c => e != null && c.Email != null && c.Email.ToLower() == e && p != null && c.PhoneNumber == p)
            .ThenBy(c => c.CandidateId)
            .Select(c => new { c.CandidateId, c.FullName, c.Email, c.PhoneNumber, CvCount = c.Cvs.Count() })
            .ToListAsync(ct);

        var counts = rows.ToDictionary(r => r.CandidateId, r => r.CvCount);
        return DuplicateMatcher
            .Find(e, p, rows.Select(r => new ExistingCandidate(r.CandidateId, r.FullName, r.Email, r.PhoneNumber)), excludeCandidateId)
            .Select(m => new DuplicateMatchDto
            {
                CandidateId = m.CandidateId,
                FullName = m.FullName,
                Email = m.Email,
                PhoneNumber = m.PhoneNumber,
                MatchedOn = m.MatchedOn.ToList(),
                CvCount = counts[m.CandidateId]
            })
            .ToList();
    }
}
