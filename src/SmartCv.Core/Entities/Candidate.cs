namespace SmartCv.Core.Entities;

/// <summary>Bảng Candidates - Ứng viên.</summary>
public class Candidate
{
    public int CandidateId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public DateTime CreatedAt { get; set; }

    // 1 ứng viên có nhiều CV
    public ICollection<Cv> Cvs { get; set; } = new List<Cv>();
}
