using System.Text;
using SmartCv.Core.Evaluation;

namespace SmartCv.Tests;

public class HrLabelCsvParserTests
{
    [Theory]
    [InlineData("Đạt", true)]
    [InlineData("dat", true)]
    [InlineData("PASS", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("Không đạt", false)]
    [InlineData("khong dat", false)]
    [InlineData("fail", false)]
    [InlineData("0", false)]
    [InlineData("Loại", false)]
    public void Labels_support_documented_vietnamese_and_english_values(string text, bool expected) =>
        Assert.Equal(expected, HrLabelCsvParser.ParsePass(text));

    [Theory]
    [InlineData(",", false)]
    [InlineData(";", true)]
    public void Unicode_headers_bom_and_separators_parse_without_losing_notes(string separator, bool bom)
    {
        var csv = (bom ? "\uFEFF" : "") + $"Mã hồ sơ{separator}Kết quả{separator}Ghi chú\r\n"
            + $"1{separator}Đạt{separator}Có kinh nghiệm thực tế\r\n2{separator}Không đạt{separator}\r\n";
        var parsed = HrLabelCsvParser.Parse(csv);
        Assert.True(parsed.IsValid, string.Join(" | ", parsed.Errors));
        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal("Có kinh nghiệm thực tế", parsed.Rows[0].Note);
        Assert.Null(parsed.Rows[1].Note);
        Assert.False(parsed.Rows[1].IsPass);
    }

    [Fact]
    public void Quoted_multiline_values_and_escaped_quotes_keep_physical_source_line_numbers()
    {
        var parsed = HrLabelCsvParser.Parse("ApplicationId;KetQua;GhiChu\r\n1;Đạt;\"Có dự án; \"\"tốt\"\"\r\nvà thực tập\"\r\n2;fail;Thiếu SQL");
        Assert.True(parsed.IsValid, string.Join(" | ", parsed.Errors));
        Assert.Equal("Có dự án; \"tốt\"\r\nvà thực tập", parsed.Rows[0].Note);
        Assert.Equal(2, parsed.Rows[0].LineNumber);
        Assert.Equal(4, parsed.Rows[1].LineNumber);
    }

    [Fact]
    public void Multiline_note_does_not_shift_validation_error_to_wrong_csv_line()
    {
        var parsed = HrLabelCsvParser.Parse("ApplicationId,IsPass,Note\n1,pass,\"line one\nline two\"\n2,maybe,bad");
        Assert.False(parsed.IsValid);
        Assert.Empty(parsed.Rows);
        Assert.Contains(parsed.Errors, e => e.Contains("Dòng 4") && e.Contains("maybe"));
    }

    [Theory]
    [InlineData("ApplicationId;KetQua;GhiChu\n1;pass;\"unterminated")]
    [InlineData("ApplicationId;KetQua;GhiChu\n1;pass;un\"quoted")]
    [InlineData("ApplicationId;KetQua;GhiChu\n1;pass;\"closed\"suffix")]
    [InlineData("ApplicationId;KetQua\n1;pass;extra")]
    [InlineData("ApplicationId;KetQua;GhiChu\n1;pass")]
    public void Malformed_csv_is_rejected_without_usable_partial_rows(string csv)
    {
        var result = HrLabelCsvParser.Parse(csv);
        Assert.False(result.IsValid);
        Assert.Empty(result.Rows);
        Assert.Contains(result.Errors, e => e.Contains("Dòng 2"));
    }

    [Theory]
    [InlineData("ApplicationId;GhiChu\n1;note")]
    [InlineData("KetQua;GhiChu\npass;note")]
    [InlineData("ApplicationId;KetQua;Kết quả\n1;pass;fail")]
    [InlineData("ApplicationId;KetQua;KetQua\n1;pass;fail")]
    public void Missing_or_ambiguous_required_columns_are_rejected(string csv)
    {
        var result = HrLabelCsvParser.Parse(csv);
        Assert.False(result.IsValid);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Filename_only_rows_keep_the_name_for_unambiguous_database_resolution()
    {
        var result = HrLabelCsvParser.Parse("Tên file,Kết quả,Ghi chú\n\"CV, ứng viên.pdf\",pass,Đủ kỹ năng");
        Assert.True(result.IsValid);
        var row = Assert.Single(result.Rows);
        Assert.Null(row.ApplicationId);
        Assert.Equal("CV, ứng viên.pdf", row.FileName);
    }

    [Fact]
    public void Blank_results_and_empty_records_are_skipped_but_errors_keep_original_lines()
    {
        var result = HrLabelCsvParser.Parse("ApplicationId;KetQua\n1;\n;\n2;pass\n\n2;fail");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Dòng 6") && e.Contains("dòng 4"));
    }

    [Fact]
    public void Duplicate_filename_matching_is_case_insensitive()
    {
        var result = HrLabelCsvParser.Parse("TenFile;KetQua\nResume.pdf;pass\nresume.PDF;fail");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Dòng 3") && e.Contains("dòng 2"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    public void Application_id_must_be_a_positive_integer(string id)
    {
        var result = HrLabelCsvParser.Parse($"ApplicationId,IsPass\n{id},pass");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Dòng 2"));
    }

    [Fact]
    public void Oversized_notes_are_rejected_instead_of_silently_truncated()
    {
        var result = HrLabelCsvParser.Parse("ApplicationId;KetQua;GhiChu\n1;pass;" + new string('a', 501));
        Assert.False(result.IsValid);
        Assert.Empty(result.Rows);
        Assert.Contains(result.Errors, e => e.Contains("500"));
    }

    [Fact]
    public void More_than_two_thousand_labels_rejects_the_whole_document()
    {
        var csv = new StringBuilder("ApplicationId;KetQua\n");
        for (var i = 1; i <= 2001; i++) csv.Append(i).AppendLine(";pass");
        var result = HrLabelCsvParser.Parse(csv.ToString());
        Assert.False(result.IsValid);
        Assert.Empty(result.Rows);
        Assert.Contains(result.Errors, e => e.Contains("Dòng 2002") && e.Contains("2000"));
    }

    [Fact]
    public void Exactly_two_thousand_labels_and_a_later_undecided_row_are_allowed()
    {
        var csv = new StringBuilder("ApplicationId;KetQua\n");
        for (var i = 1; i <= 2000; i++) csv.Append(i).AppendLine(";pass");
        csv.AppendLine("2001;");
        var result = HrLabelCsvParser.Parse(csv.ToString());
        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Equal(2000, result.Rows.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\uFEFF\n\r\n")]
    [InlineData("ApplicationId;KetQua\n1;\n")]
    public void Empty_files_or_no_decided_labels_are_invalid(string? csv) => Assert.False(HrLabelCsvParser.Parse(csv).IsValid);

    [Fact]
    public void Binary_nul_content_is_rejected_as_invalid_csv()
    {
        var result = HrLabelCsvParser.Parse("ApplicationId;KetQua\0\n1;pass");
        Assert.False(result.IsValid);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Template_round_trip_has_bom_and_preserves_normal_vietnamese_notes()
    {
        var template = HrLabelCsvParser.BuildTemplate(
            [(7, "CV;\"Hà\".pdf", "Trần Hà", "Backend", true, "Có dự án\nvà thực tập"),
             (8, "other.pdf", null, "AI", null, null)]);
        Assert.StartsWith("\uFEFFApplicationId;TenFile;UngVien;ViTri;KetQua;GhiChu\r\n", template);
        var result = HrLabelCsvParser.Parse(template);
        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        var row = Assert.Single(result.Rows);
        Assert.Equal(7, row.ApplicationId);
        Assert.Equal("CV;\"Hà\".pdf", row.FileName);
        Assert.Equal("Có dự án\nvà thực tập", row.Note);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://example.test\")")]
    [InlineData("+SUM(1,2)")]
    [InlineData("-1+2")]
    [InlineData("@SUM(1)")]
    [InlineData("  =1+1")]
    [InlineData("\tunsafe")]
    public void Export_escapes_potential_excel_formulas_in_all_text_fields(string text)
    {
        var template = HrLabelCsvParser.BuildTemplate([(1, text, text, text, true, text)]);
        Assert.DoesNotContain(";" + text + ";", template);
        Assert.Contains("'", template);
        var result = HrLabelCsvParser.Parse(template);
        Assert.True(result.IsValid);
        Assert.StartsWith("'", Assert.Single(result.Rows).Note);
    }
}
