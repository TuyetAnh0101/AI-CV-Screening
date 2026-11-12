using System.IO.Compression;
using System.Text;
using SmartCv.Core.Extraction;
using SmartCv.Core.Privacy;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Xunit.Abstractions;

namespace SmartCv.Tests;

/// <summary>
/// A37 trên 20 CV PDF mẫu của gói hướng dẫn (thông tin giả, tự tạo). Chạy khi đặt SMARTCV_B34_DEMO_ZIP (cùng gói với B34).
/// Đọc chữ giống CvTextExtractor (PdfPig ContentOrderTextExtractor + TextCleaner), che như khi CV chưa gắn ứng viên
/// (tên đoán từ dòng đầu): chỉ dòng tên và dòng liên hệ thay đổi, mọi dòng chuyên môn giữ nguyên từng ký tự.
/// </summary>
public class SampleCvMaskingTests(ITestOutputHelper output)
{
    [B34DemoFact]
    public void Twenty_sample_cvs_mask_contact_and_name_and_keep_every_other_line()
    {
        using var zip = ZipFile.OpenRead(Environment.GetEnvironmentVariable("SMARTCV_B34_DEMO_ZIP")!);
        var pdfs = zip.Entries.Where(e => e.FullName.Contains("cv-20-backend/") && e.Name.EndsWith(".pdf")).OrderBy(e => e.Name).ToList();
        Assert.Equal(20, pdfs.Count);

        int emails = 0, phones = 0, links = 0, names = 0, unchangedLines = 0;
        foreach (var entry in pdfs)
        {
            var text = Extract(entry);
            var r = PersonalDataMasker.Mask(text);

            Assert.True((1, 1, 1) == (r.Emails, r.Phones, r.Links), $"{entry.Name}: email {r.Emails}, phone {r.Phones}, link {r.Links}");
            Assert.True(r.Names >= 1, $"{entry.Name}: không đoán được tên");
            Assert.DoesNotContain("@", r.Text);
            Assert.DoesNotContain("example.com", r.Text);
            Assert.DoesNotContain("linkedin.com", r.Text);

            var original = text.Split('\n');
            var masked = r.Text.Split('\n');
            Assert.Equal(original.Length, masked.Length);
            Assert.Equal("[CANDIDATE]", masked[0]);
            for (var i = 1; i < original.Length; i++)
            {
                if (original[i].StartsWith("Email:", StringComparison.Ordinal))
                    Assert.Equal("Email: [EMAIL] | Điện thoại: [PHONE] | [LINK]", masked[i]);
                else
                {
                    Assert.Equal(original[i], masked[i]);
                    unchangedLines++;
                }
            }
            (emails, phones, links, names) = (emails + r.Emails, phones + r.Phones, links + r.Links, names + r.Names);
        }

        output.WriteLine($"20 CV: email {emails}, điện thoại {phones}, link {links}, họ tên {names}; {unchangedLines} dòng chuyên môn giữ nguyên.");
    }

    private static string Extract(ZipArchiveEntry entry)
    {
        using var buffer = new MemoryStream();
        using (var stream = entry.Open()) stream.CopyTo(buffer);
        using var document = PdfDocument.Open(buffer.ToArray());
        var sb = new StringBuilder();
        foreach (var page in document.GetPages()) sb.AppendLine(ContentOrderTextExtractor.GetText(page));
        return TextCleaner.Clean(sb.ToString()).ReplaceLineEndings("\n");
    }
}
