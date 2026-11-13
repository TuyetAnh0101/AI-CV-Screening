using SmartCv.Core.Privacy;

namespace SmartCv.Core.Ai;

/// <summary>
/// Toàn bộ phần chữ gửi AI cho một CV.
/// Mask: kết quả che thông tin cá nhân (null khi Gemini:MaskPersonalData = false).
/// CvCharacters: độ dài CV gốc; CvCharactersSent: độ dài phần CV thật sự nằm trong UserPrompt (sau che và cắt).
/// AllowedSkillCodes: mã kỹ năng của job, cũng là danh sách enum trong responseSchema gửi Gemini.
/// </summary>
public sealed record AssembledPrompt(
    string SystemPrompt,
    string UserPrompt,
    bool PersonalDataMasked,
    MaskResult? Mask,
    int CvCharacters,
    int CvCharactersSent,
    bool Truncated,
    IReadOnlyList<string> AllowedSkillCodes);

/// <summary>
/// A41: MỘT nguồn sự thật cho nội dung gửi AI. GeminiEvaluationService (gửi thật) và API assembled-preview (xem trước)
/// đều gọi hàm này với cùng cấu hình, nên nội dung xem trước luôn giống hệt nội dung gửi đi.
/// Thứ tự giữ nguyên như A37: che thông tin cá nhân MỘT lần trên CV gốc, rồi mới cắt bớt và ghép prompt (AiPromptBuilder).
/// Hàm thuần: không gọi mạng, không ghi log (người gọi chỉ được ghi các con số trong <see cref="MaskResult"/>).
/// </summary>
public static class AiPromptAssembler
{
    public static AssembledPrompt Assemble(AiEvaluationRequest request, int maxCvCharacters, bool maskPersonalData)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Job);

        var original = request.CvText ?? string.Empty;
        MaskResult? mask = null;
        var cvText = original;
        if (maskPersonalData)
        {
            mask = PersonalDataMasker.Mask(original, request.CandidateName);
            cvText = mask.Text;
        }

        return new AssembledPrompt(
            request.SystemPrompt,
            AiPromptBuilder.BuildUserPrompt(request.Job, cvText, maxCvCharacters, maskPersonalData),
            maskPersonalData,
            mask,
            original.Length,
            AiPromptBuilder.Limit(cvText, maxCvCharacters).Length,
            AiPromptBuilder.IsTruncated(cvText, maxCvCharacters),
            request.Job.Skills.Select(s => s.SkillCode).ToArray());
    }
}
