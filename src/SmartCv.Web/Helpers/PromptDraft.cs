using System.Numerics;
using SmartCv.Core.Contracts;

namespace SmartCv.Web.Helpers;

/// <summary>B12: a separate draft; existing API versions are never mutated.</summary>
public sealed class PromptDraft
{
    // A14 POST /api/prompts limits. Keep in sync with PromptEndpoints and PromptContracts.
    public const int MaxNameLength = 100;
    public const int MaxPromptLength = 20_000;
    public const int MaxNoteLength = 500;

    private string content = string.Empty;
    private (int Added, int Removed)? changes;

    public PromptVersionDto? Source { get; private set; }
    public string Content
    {
        get => content;
        set
        {
            if (content == value) return;
            content = value ?? string.Empty;
            changes = null;
        }
    }
    public string VersionName { get; set; } = string.Empty;
    public string ChangeNote { get; set; } = string.Empty;

    // Browser textareas normalize line endings; that alone is not an edit.
    public bool ContentChanged => !string.Equals(Normalize(Content), Normalize(Source?.SystemPrompt ?? string.Empty), StringComparison.Ordinal);
    public bool IsDirty => ContentChanged || VersionName.Length > 0 || ChangeNote.Length > 0;
    public int LineCount => 1 + Normalize(Content).Count(c => c == '\n');
    public (int Added, int Removed) Changes => changes ??= CountChanges(Source?.SystemPrompt ?? string.Empty, Content);

    public void Load(PromptVersionDto? source)
    {
        Source = source;
        Reset();
    }

    public void Reset()
    {
        Content = Source?.SystemPrompt ?? string.Empty;
        VersionName = string.Empty;
        ChangeNote = string.Empty;
        changes = null;
    }

    public string? Validate(IReadOnlyList<PromptVersionDto> versions)
    {
        var name = VersionName.Trim();
        if (name.Length == 0 || name.EndsWith('-'))
            return "Hãy đặt tên phiên bản hoàn chỉnh, ví dụ v3-du-an-thuc-tap.";
        if (name.Length > MaxNameLength)
            return $"Tên phiên bản tối đa {MaxNameLength} ký tự.";
        if (versions.Any(v => string.Equals(v.VersionName.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            return $"Đã có phiên bản tên \"{name}\".";
        if (string.IsNullOrWhiteSpace(Content)) return "Nội dung system prompt không được để trống.";
        if (Content.Length > MaxPromptLength) return $"Nội dung system prompt tối đa {MaxPromptLength} ký tự.";
        if (ChangeNote.Trim().Length > MaxNoteLength) return $"Ghi chú thay đổi tối đa {MaxNoteLength} ký tự.";
        if (!ContentChanged) return "Nội dung giống bản gốc. Hãy sửa prompt trước khi lưu.";

        // A14 ignores runs of whitespace when rejecting duplicate content across all versions.
        var key = Squash(Content);
        var duplicate = versions.FirstOrDefault(v => Squash(v.SystemPrompt) == key);
        return duplicate is null ? null
            : $"Nội dung giống phiên bản \"{duplicate.VersionName}\" (chỉ khác khoảng trắng). Hãy sửa prompt trước khi lưu.";
    }

    public CreatePromptVersionRequest ToRequest() => new()
    {
        VersionName = VersionName.Trim(),
        SystemPrompt = Content, // Preserve draft text; A14 owns its existing storage normalization.
        ChangeNote = string.IsNullOrWhiteSpace(ChangeNote) ? null : ChangeNote.Trim()
    };

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
    private static string Squash(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Exact insertion/deletion counts from an ordered longest common subsequence.
    /// Bit-parallel LCS avoids an N×M integer matrix for a 20,000-character multiline prompt.
    /// Each bit records where a prefix's LCS length increases; popcount gives the final length.
    /// Blank and repeated lines are significant, and moving a line counts as a removal/addition.
    /// </summary>
    private static (int Added, int Removed) CountChanges(string original, string draft)
    {
        var oldLines = Normalize(original).Split('\n');
        var newLines = Normalize(draft).Split('\n');
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
        var oldEnd = oldLines.Length;
        var newEnd = newLines.Length;
        while (oldEnd > prefix && newEnd > prefix && oldLines[oldEnd - 1] == newLines[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }
        var oldCount = oldEnd - prefix;
        var newCount = newEnd - prefix;
        if (oldCount == 0 || newCount == 0) return (newCount, oldCount);

        var masks = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
        for (var i = prefix; i < oldEnd; i++)
        {
            masks.TryGetValue(oldLines[i], out var bits);
            masks[oldLines[i]] = bits | (BigInteger.One << (i - prefix));
        }
        var state = BigInteger.Zero;
        for (var i = prefix; i < newEnd; i++)
        {
            masks.TryGetValue(newLines[i], out var matches);
            var union = matches | state;
            state = union & ~(union - ((state << 1) | BigInteger.One));
        }
        var common = state.ToByteArray(isUnsigned: true).Sum(b => BitOperations.PopCount((uint)b));
        return (newCount - common, oldCount - common);
    }
}
