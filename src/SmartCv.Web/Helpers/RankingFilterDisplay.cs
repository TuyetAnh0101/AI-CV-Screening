namespace SmartCv.Web.Helpers;

/// <summary>
/// FIX-B56-UI: chỉ tổng hợp trạng thái bộ lọc để trình bày, không thay đổi
/// RankingView, điểm AI, giai đoạn HR, logic chọn hàng hay xuất kết quả.
/// </summary>
public static class RankingFilterDisplay
{
    public static int ActiveCount(bool hasSearch, bool hasAiFilter,
        bool hasHrStageFilter, bool needsReviewOnly, bool hasManualFlagOnly) =>
        (hasSearch ? 1 : 0) + (hasAiFilter ? 1 : 0) +
        (hasHrStageFilter ? 1 : 0) + (needsReviewOnly ? 1 : 0) +
        (hasManualFlagOnly ? 1 : 0);

    public static string CountLabel(int visible, int total)
    {
        if (visible < 0 || total < 0 || visible > total)
            throw new ArgumentOutOfRangeException(nameof(visible), "Số hồ sơ không hợp lệ.");
        return $"{visible:N0}/{total:N0} hồ sơ";
    }
}
