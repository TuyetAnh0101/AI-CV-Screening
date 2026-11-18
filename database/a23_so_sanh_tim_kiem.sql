/* A23: đếm bằng SQL để so với totalCount của GET /api/cvs?search=... (đổi @Search). */
DECLARE @Search NVARCHAR(100) = N'React';
SELECT COUNT(*) AS SoCvKhop
FROM Cvs v
LEFT JOIN Candidates c ON c.CandidateId = v.CandidateId
WHERE v.AnonymizedAt IS NULL
  AND (v.OriginalFileName LIKE N'%' + @Search + N'%'
       OR c.FullName LIKE N'%' + @Search + N'%'
       OR c.Email LIKE N'%' + @Search + N'%'
       OR v.ExtractedText LIKE N'%' + @Search + N'%');

-- Collation của cột (quyết định có phân biệt hoa thường / dấu không): ..._CI_AS = KHÔNG phân biệt hoa thường, CÓ phân biệt dấu
SELECT name, collation_name FROM sys.columns WHERE object_id = OBJECT_ID('Cvs') AND name = 'ExtractedText';
