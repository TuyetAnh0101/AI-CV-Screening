using System.Globalization;
using System.Text;
using SmartCv.Core.Scoring;

namespace SmartCv.Core.Evaluation;

public sealed record HrLabelCsvRow(int LineNumber, int? ApplicationId, string? FileName, bool IsPass, string? Note);

public sealed record HrLabelCsvResult(IReadOnlyList<HrLabelCsvRow> Rows, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// A15: CSV UTF-8 from Excel, with comma/semicolon delimiters and optional UTF-8 BOM.
/// Empty results are undecided and skipped. An invalid document returns no usable rows.
/// Line numbers refer to physical source lines, including newlines inside quoted notes.
/// </summary>
public static class HrLabelCsvParser
{
    public const int MaxRows = 2000;
    public const int MaxNoteLength = 500;

    private static readonly string[] IdHeaders = ["applicationid", "mahoso", "idhoso", "id"];
    private static readonly string[] FileHeaders = ["filename", "tenfile", "file", "tep", "tentep"];
    private static readonly string[] PassHeaders = ["ispass", "ketqua", "nhan", "nhanhr", "label", "danhgia", "pass"];
    private static readonly string[] NoteHeaders = ["note", "ghichu", "nhanxet", "lydo"];
    private static readonly string[] PassValues = ["dat", "1", "true", "pass", "yes", "co", "x", "d"];
    private static readonly string[] FailValues = ["khongdat", "0", "false", "fail", "no", "khong", "loai", "chuadat", "kd"];

    public static HrLabelCsvResult Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new([], ["File rỗng."]);
        if (content.Contains('\0')) return new([], ["File CSV chứa ký tự NUL không hợp lệ."]);
        content = content.TrimStart('\uFEFF');
        var errors = new List<string>();
        var records = ReadRecords(content, DetectSeparator(content), errors);
        if (errors.Count > 0) return new([], errors);
        records = records.Where(r => r.Fields.Any(f => !string.IsNullOrWhiteSpace(f))).ToList();
        if (records.Count == 0) return new([], ["File rỗng."]);

        var header = records[0];
        var keys = header.Fields.Select(Key).ToList();
        var duplicate = keys.Where(k => k.Length > 0).GroupBy(k => k).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) errors.Add($"Dòng {header.LineNumber}: tiêu đề cột bị trùng.");
        int Column(string[] aliases, string label)
        {
            var indices = keys.Select((key, index) => (key, index)).Where(x => aliases.Contains(x.key)).ToList();
            if (indices.Count > 1) errors.Add($"Dòng {header.LineNumber}: có nhiều cột {label}.");
            return indices.Count == 0 ? -1 : indices[0].index;
        }
        var idCol = Column(IdHeaders, "mã hồ sơ");
        var fileCol = Column(FileHeaders, "tên file");
        var passCol = Column(PassHeaders, "Kết quả");
        var noteCol = Column(NoteHeaders, "Ghi chú");
        if (passCol < 0) errors.Add("Thiếu cột Kết quả (KetQua / IsPass).");
        if (idCol < 0 && fileCol < 0) errors.Add("Thiếu cột mã hồ sơ (ApplicationId) hoặc tên file (TenFile).");
        if (errors.Count > 0) return new([], errors);

        var rows = new List<HrLabelCsvRow>();
        var seenIds = new Dictionary<int, int>();
        var seenFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var labeledRows = 0;
        foreach (var record in records.Skip(1))
        {
            var line = record.LineNumber;
            if (record.Fields.Count != header.Fields.Count)
            {
                errors.Add($"Dòng {line}: số cột không khớp tiêu đề ({record.Fields.Count}/{header.Fields.Count}).");
                continue;
            }
            string Cell(int column) => column < 0 ? string.Empty : record.Fields[column].Trim();
            var resultText = Cell(passCol);
            if (resultText.Length == 0) continue;
            if (++labeledRows > MaxRows)
            {
                errors.Add($"Dòng {line}: file vượt quá {MaxRows} nhãn; không nhập dữ liệu.");
                break;
            }
            var isPass = ParsePass(resultText);
            if (isPass is null)
            {
                errors.Add($"Dòng {line}: không hiểu kết quả \"{resultText}\" (dùng Đạt / Không đạt).");
                continue;
            }
            int? id = null;
            var idText = Cell(idCol);
            if (idText.Length > 0)
            {
                if (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                {
                    errors.Add($"Dòng {line}: mã hồ sơ \"{idText}\" không phải số nguyên dương.");
                    continue;
                }
                id = parsed;
            }
            var file = Cell(fileCol);
            if (id is null && file.Length == 0)
            {
                errors.Add($"Dòng {line}: thiếu cả mã hồ sơ và tên file.");
                continue;
            }
            var note = Cell(noteCol);
            if (note.Length > MaxNoteLength)
            {
                errors.Add($"Dòng {line}: ghi chú vượt quá {MaxNoteLength} ký tự.");
                continue;
            }
            if (id is { } applicationId)
            {
                if (!seenIds.TryAdd(applicationId, line))
                {
                    errors.Add($"Dòng {line}: hồ sơ {applicationId} đã có ở dòng {seenIds[applicationId]}.");
                    continue;
                }
            }
            else if (!seenFiles.TryAdd(file, line))
            {
                errors.Add($"Dòng {line}: file \"{file}\" đã có ở dòng {seenFiles[file]}.");
                continue;
            }
            rows.Add(new(line, id, NullIfEmpty(file), isPass.Value, NullIfEmpty(note)));
        }
        if (rows.Count == 0 && errors.Count == 0) errors.Add("Không có dòng nào có Kết quả.");
        return new(errors.Count == 0 ? rows : [], errors);
    }

    public static bool? ParsePass(string? text)
    {
        var key = Key(text);
        if (FailValues.Contains(key)) return false;
        if (PassValues.Contains(key)) return true;
        return null;
    }

    /// <summary>
    /// Excel-friendly UTF-8 BOM and semicolon CSV. Potential spreadsheet formulas in text
    /// are prefixed with an apostrophe on export; import deliberately does not remove user text.
    /// </summary>
    public static string BuildTemplate(IEnumerable<(int ApplicationId, string FileName, string? CandidateName,
        string JobTitle, bool? IsPass, string? Note)> rows)
    {
        var csv = new StringBuilder("\uFEFFApplicationId;TenFile;UngVien;ViTri;KetQua;GhiChu\r\n");
        foreach (var row in rows)
        {
            var result = row.IsPass switch { true => "Đạt", false => "Không đạt", _ => string.Empty };
            csv.Append(string.Join(';', row.ApplicationId.ToString(CultureInfo.InvariantCulture), Quote(row.FileName),
                Quote(row.CandidateName), Quote(row.JobTitle), result, Quote(row.Note))).Append("\r\n");
        }
        return csv.ToString();
    }

    private static string Key(string? value)
    {
        var folded = TextNormalizer.Normalize(TextNormalizer.Compose(value?.Trim()));
        return new string(folded.Where(char.IsLetterOrDigit).ToArray());
    }

    private static char DetectSeparator(string content)
    {
        var quoted = false;
        var commas = 0;
        var semicolons = 0;
        foreach (var c in content)
        {
            if (c == '"') quoted = !quoted;
            if (quoted) continue;
            if (c == ',') commas++;
            else if (c == ';') semicolons++;
            else if ((c is '\r' or '\n') && commas + semicolons > 0) break;
        }
        return semicolons > commas ? ';' : ',';
    }

    private sealed record CsvRecord(int LineNumber, List<string> Fields);

    private static List<CsvRecord> ReadRecords(string content, char separator, List<string> errors)
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closedQuote = false;
        var line = 1;
        var startLine = 1;
        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            closedQuote = false;
        }
        void EndRecord()
        {
            EndField();
            records.Add(new(startLine, fields));
            fields = [];
        }
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; closedQuote = true; }
                }
                else
                {
                    field.Append(c);
                    if (c == '\r')
                    {
                        if (i + 1 < content.Length && content[i + 1] == '\n') field.Append(content[++i]);
                        line++;
                    }
                    else if (c == '\n') line++;
                }
                continue;
            }
            if (c == separator) { EndField(); continue; }
            if (c is '\r' or '\n')
            {
                EndRecord();
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                startLine = ++line;
                continue;
            }
            if (c == '"')
            {
                if (closedQuote || !string.IsNullOrWhiteSpace(field.ToString()))
                {
                    errors.Add($"Dòng {line}: dấu ngoặc kép không hợp lệ.");
                    return records;
                }
                field.Clear();
                quoted = true;
            }
            else if (closedQuote)
            {
                if (!char.IsWhiteSpace(c))
                {
                    errors.Add($"Dòng {line}: có ký tự sau dấu ngoặc kép đóng.");
                    return records;
                }
            }
            else field.Append(c);
        }
        if (quoted) errors.Add($"Dòng {startLine}: chưa đóng dấu ngoặc kép.");
        else if (fields.Count > 0 || field.Length > 0 || closedQuote) EndRecord();
        return records;
    }

    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var first = value.AsSpan().TrimStart();
        if ((first.Length > 0 && first[0] is '=' or '+' or '-' or '@') || value[0] is '\t' or '\r' or '\n')
            value = "'" + value;
        return value.IndexOfAny([';', ',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
