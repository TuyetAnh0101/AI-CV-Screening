extern alias web;

using SmartCv.Core.Contracts;
using web::SmartCv.Web.Helpers;

namespace SmartCv.Tests;

public class PromptDraftTests
{
    private static PromptVersionDto Version(string content = "Vai trò\nQuy tắc", string name = "v2-dang-dung", int id = 2) => new()
    {
        PromptVersionId = id, VersionName = name, SystemPrompt = content, IsActive = true
    };

    private static PromptDraft Draft(string original = "Vai trò\nQuy tắc", string? edited = null)
    {
        var draft = new PromptDraft();
        draft.Load(Version(original));
        draft.VersionName = "v3-quy-tac-moi";
        draft.Content = edited ?? original + "\nBằng chứng";
        return draft;
    }

    [Theory]
    [InlineData("a\nb", "a\nb\nc", 1, 0)]
    [InlineData("a\nb\nc", "a\nc", 0, 1)]
    [InlineData("a\nb", "a\nc", 1, 1)]
    [InlineData("a\nb", "b\na", 1, 1)]
    [InlineData("a\na\nb", "a\nb\na", 1, 1)]
    [InlineData("a\nb", "a\n\nb", 1, 0)]
    [InlineData("a\n\nb", "a\nb", 0, 1)]
    [InlineData("a\nb", "a\nb\n", 1, 0)]
    [InlineData("a\r\nb", "a\nb", 0, 0)]
    [InlineData("a\nb ", "a\nb", 1, 1)]
    public void Diff_preserves_order_duplicates_blank_lines_and_significant_spaces(string original, string current, int added, int removed)
    {
        var draft = Draft(original, current);
        Assert.Equal((added, removed), draft.Changes);
    }

    [Fact]
    public void Line_endings_are_equivalent_for_dirty_state_but_preserved_in_request()
    {
        var source = Version("Vai trò\r\nQuy tắc\r\n");
        var draft = new PromptDraft();
        draft.Load(source);
        draft.Content = "Vai trò\nQuy tắc\n";
        Assert.False(draft.ContentChanged);
        Assert.False(draft.IsDirty);
        Assert.Equal(3, draft.LineCount);
        draft.Content += "Thêm mới\r\n";
        Assert.True(draft.ContentChanged);
        Assert.Equal(draft.Content, draft.ToRequest().SystemPrompt);
        Assert.Equal("Vai trò\r\nQuy tắc\r\n", source.SystemPrompt);
    }

    [Fact]
    public void Reset_restores_selected_source_not_original_active_and_clears_metadata()
    {
        var draft = Draft();
        var old = Version("Bản cũ", "v1-cu", 1);
        draft.Load(old);
        draft.Content = "Thay đổi";
        draft.VersionName = "v3-moi";
        draft.ChangeNote = "Ghi chú";
        Assert.True(draft.IsDirty);
        draft.Reset();
        Assert.Same(old, draft.Source);
        Assert.Equal(old.SystemPrompt, draft.Content);
        Assert.Equal(string.Empty, draft.VersionName);
        Assert.Equal(string.Empty, draft.ChangeNote);
        Assert.Equal((0, 0), draft.Changes);
        Assert.False(draft.IsDirty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v3-")]
    [InlineData(" v3-  ")]
    public void Incomplete_name_is_rejected(string name)
    {
        var draft = Draft();
        draft.VersionName = name;
        Assert.Contains("hoàn chỉnh", draft.Validate([]));
    }

    [Fact]
    public void Duplicate_names_are_case_insensitive_and_trimmed()
    {
        var draft = Draft();
        draft.VersionName = " V2-DANG-DUNG ";
        Assert.Contains("Đã có", draft.Validate([Version()]));
    }

    [Fact]
    public void Name_prompt_and_note_limits_match_A14()
    {
        var draft = Draft();
        draft.VersionName = new string('v', PromptDraft.MaxNameLength);
        draft.Content = new string('a', PromptDraft.MaxPromptLength);
        draft.ChangeNote = new string('n', PromptDraft.MaxNoteLength);
        Assert.Null(draft.Validate([]));
        draft.VersionName += "v";
        Assert.Contains("100", draft.Validate([]));
        draft.VersionName = "v3-hop-le";
        draft.Content += "a";
        Assert.Contains("20000", draft.Validate([]));
        draft.Content = "Nội dung mới";
        draft.ChangeNote += "n";
        Assert.Contains("500", draft.Validate([]));
    }

    [Fact]
    public void Unchanged_empty_and_whitespace_duplicate_content_are_rejected()
    {
        var draft = Draft();
        draft.Content = draft.Source!.SystemPrompt;
        Assert.Contains("bản gốc", draft.Validate([]));
        draft.Content = " \r\n \t ";
        Assert.Contains("để trống", draft.Validate([]));
        draft.Content = "Vai trò  \n  Quy tắc";
        Assert.True(draft.ContentChanged);
        Assert.Contains("khoảng trắng", draft.Validate([Version()]));
        draft.Content = "Nội dung của v1";
        Assert.Contains("v1-cu", draft.Validate([Version(draft.Content, "v1-cu", 1)]));
    }

    [Fact]
    public void Reordered_lines_can_be_saved_and_request_does_not_change_source()
    {
        var draft = Draft("a\nb", "b\na");
        Assert.True(draft.ContentChanged);
        Assert.Null(draft.Validate([draft.Source!]));
        draft.VersionName = " v3-moi ";
        draft.ChangeNote = " ghi chú ";
        var request = draft.ToRequest();
        Assert.Equal("b\na", request.SystemPrompt);
        Assert.Equal("v3-moi", request.VersionName);
        Assert.Equal("ghi chú", request.ChangeNote);
        Assert.Equal("a\nb", draft.Source!.SystemPrompt);
    }

    [Fact]
    public void Empty_initial_state_supports_first_version_and_metadata_is_dirty()
    {
        var draft = new PromptDraft();
        draft.Load(null);
        Assert.Equal(1, draft.LineCount);
        Assert.False(draft.IsDirty);
        draft.VersionName = "v1-moi";
        Assert.True(draft.IsDirty);
        draft.Content = "Bạn là chuyên gia tuyển dụng.";
        Assert.Null(draft.Validate([]));
        draft.Reset();
        Assert.False(draft.IsDirty);
        draft.ChangeNote = "Ghi chú chưa lưu";
        Assert.True(draft.IsDirty);
    }

    [Fact]
    public void Large_prompt_diff_is_exact_without_quadratic_matrix()
    {
        var draft = Draft(string.Join('\n', Enumerable.Repeat("a", 10_000)),
            string.Join('\n', Enumerable.Repeat("b", 10_000)));
        Assert.Equal((10_000, 10_000), draft.Changes);
        Assert.Equal(10_000, draft.LineCount);
    }

    [Fact]
    public void Ordered_diff_matches_reference_LCS_for_repeated_and_blank_random_lines()
    {
        var random = new Random(12);
        var alphabet = new[] { "", "a", "b", "c", "a " };
        for (var example = 0; example < 100; example++)
        {
            var oldLines = Enumerable.Range(0, random.Next(1, 15)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
            var newLines = Enumerable.Range(0, random.Next(1, 15)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
            var lengths = new int[oldLines.Length + 1, newLines.Length + 1];
            for (var i = 1; i <= oldLines.Length; i++)
                for (var j = 1; j <= newLines.Length; j++)
                    lengths[i, j] = oldLines[i - 1] == newLines[j - 1]
                        ? lengths[i - 1, j - 1] + 1 : Math.Max(lengths[i - 1, j], lengths[i, j - 1]);
            var common = lengths[oldLines.Length, newLines.Length];
            var draft = Draft(string.Join('\n', oldLines), string.Join('\n', newLines));
            Assert.Equal((newLines.Length - common, oldLines.Length - common), draft.Changes);
        }
    }
}
