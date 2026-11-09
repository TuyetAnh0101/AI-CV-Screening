using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Infrastructure.Scoring;

/// <summary>Đọc thông tin job (kỹ năng + từ khóa tương đương) để chấm. Dùng chung cho A9 (xem trước) và A11 (chạy nền).</summary>
public static class JobProfileQueries
{
    public static async Task<JobProfile?> LoadJobProfileAsync(this SmartCvDbContext db, int jobId, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking()
            .Where(j => j.JobId == jobId)
            .Select(j => new
            {
                j.JobId, j.Title, j.Description, j.MinGpa, j.MinYearsExperience,
                // "MustHave" < "NiceToHave": kỹ năng bắt buộc đứng trước, thứ tự ổn định giữa các lần gọi
                Skills = j.Skills.OrderBy(s => s.SkillType).ThenBy(s => s.SkillCode).Select(s => new
                {
                    s.SkillCode, s.DisplayName, s.SkillType,
                    Aliases = s.Aliases.Select(a => a.Alias).ToList()
                }).ToList()
            })
            .FirstOrDefaultAsync(ct);

        return job is null
            ? null
            : new JobProfile(job.JobId, job.Title, job.Description, job.MinGpa, job.MinYearsExperience,
                job.Skills.Select(s => new SkillProfile(s.SkillCode, s.DisplayName, s.SkillType, s.Aliases)).ToList());
    }
}
