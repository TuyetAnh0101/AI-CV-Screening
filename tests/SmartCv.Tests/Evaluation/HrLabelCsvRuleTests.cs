using SmartCv.Core.Evaluation;

// Thư mục Evaluation/ nhưng giữ namespace gốc (xem PromptAccuracyRuleTests).
namespace SmartCv.Tests;

/// <summary>
/// A18.5: đọc file CSV nhãn HR (HrLabelCsvParser, A15). Bổ sung cho HrLabelCsvParserTests (A15), nơi đã có:
/// BOM, dấu ; và ,, ngoặc kép và xuống dòng trong ô, chỉ có tên file, bỏ dòng chưa có kết quả, trùng hồ sơ / tên file,
/// giới hạn số dòng và độ dài ghi chú, xuất file mẫu rồi đọc lại.
/// </summary>
public class HrLabelCsvRuleTests
{
    [Theory]
    [InlineData("ĐẠT", true)]
    [InlineData("  Đạt  ", true)]
    [InlineData("Đ", true)]
    [InlineData("Có", true)]
    [InlineData("x", true)]
    [InlineData("yes", true)]
    [InlineData("KHÔNG ĐẠT", false)]
    [InlineData("Không  Đạt", false)]
    [InlineData("KHONG DAT", false)]
    [InlineData("Chưa đạt", false)]
    [InlineData("KĐ", false)]
    [InlineData("no", false)]
    [InlineData("false", false)]
    public void Cac_cach_ghi_Dat_Khong_dat_tuong_duong_khong_phan_biet_hoa_thuong_dau_va_khoang_trang(string text, bool expected) =>
        Assert.Equal(expected, HrLabelCsvParser.ParsePass(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("có lẽ")]
    [InlineData("đang xem xét")]
    public void Ket_qua_khong_hieu_thi_tra_null_de_bao_loi_khong_doan(string? text) =>
        Assert.Null(HrLabelCsvParser.ParsePass(text));

    [Theory]
    [InlineData("KetQua")]
    [InlineData("Kết quả")]
    [InlineData("Ket qua")]
    [InlineData("KẾT QUẢ")]
    [InlineData("IsPass")]
    [InlineData("Nhãn HR")]
    [InlineData("Đánh giá")]
    public void Ten_cot_ket_qua_co_dau_khong_dau_hoac_tieng_Anh_deu_doc_duoc(string header)
    {
        var r = HrLabelCsvParser.Parse($"ApplicationId;{header};Ghi chú\n1;Đạt;Đủ kỹ năng C#\n2;Không đạt;Thiếu SQL Server\n");

        Assert.True(r.IsValid, string.Join(" | ", r.Errors));
        Assert.Equal([(1, true, "Đủ kỹ năng C#"), (2, false, "Thiếu SQL Server")],
            r.Rows.Select(x => (x.ApplicationId!.Value, x.IsPass, x.Note!)));
    }

    [Fact]
    public void Bao_tat_ca_loi_trong_mot_lan_doc_kem_dung_so_dong()
    {
        var r = HrLabelCsvParser.Parse("ApplicationId,KetQua\n2,Có lẽ\n3,Không đạt\n3,Đạt\nabc,Đạt\n");

        Assert.False(r.IsValid);
        Assert.Empty(r.Rows);   // có lỗi thì không nhập một phần
        Assert.Equal(
        [
            "Dòng 2: không hiểu kết quả \"Có lẽ\" (dùng Đạt / Không đạt).",
            "Dòng 4: hồ sơ 3 đã có ở dòng 3.",
            "Dòng 5: mã hồ sơ \"abc\" không phải số nguyên dương."
        ], r.Errors);
    }

    [Theory]
    [InlineData("ApplicationId;GhiChu\n1;abc\n", "Thiếu cột Kết quả (KetQua / IsPass).")]
    [InlineData("KetQua;GhiChu\nĐạt;abc\n", "Thiếu cột mã hồ sơ (ApplicationId) hoặc tên file (TenFile).")]
    [InlineData("UngVien;GhiChu\nAn;abc\n", "Thiếu cột Kết quả (KetQua / IsPass).")]
    [InlineData("UngVien;GhiChu\nAn;abc\n", "Thiếu cột mã hồ sơ (ApplicationId) hoặc tên file (TenFile).")]
    public void Thieu_cot_bat_buoc_bao_loi_ro_ten_cot(string csv, string expectedError)
    {
        var r = HrLabelCsvParser.Parse(csv);

        Assert.False(r.IsValid);
        Assert.Empty(r.Rows);
        Assert.Contains(expectedError, r.Errors);
    }

    [Theory]
    [InlineData(null, "File rỗng.")]
    [InlineData("", "File rỗng.")]
    [InlineData("ApplicationId;KetQua\n", "Không có dòng nào có Kết quả.")]
    [InlineData("ApplicationId;KetQua\n1;\n2;  \n", "Không có dòng nào có Kết quả.")]
    public void File_rong_hoac_chua_co_ket_qua_nao_bao_loi_de_hieu(string? csv, string expectedError)
    {
        var r = HrLabelCsvParser.Parse(csv);

        Assert.False(r.IsValid);
        Assert.Equal([expectedError], r.Errors);
    }

    [Fact]
    public void Dong_chi_co_dau_phan_cach_duoc_bo_qua_va_so_dong_van_dung()
    {
        var r = HrLabelCsvParser.Parse("ApplicationId;KetQua\n1;Đạt\n2;\n\n;;\n3;0\n");

        Assert.True(r.IsValid, string.Join(" | ", r.Errors));
        Assert.Equal([(2, 1, true), (6, 3, false)], r.Rows.Select(x => (x.LineNumber, x.ApplicationId!.Value, x.IsPass)));
    }
}
