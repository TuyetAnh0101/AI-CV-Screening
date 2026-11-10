using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components.Forms;

namespace SmartCv.Web.Services;

/// <summary>
/// Một phần của request multipart, đọc file trên trình duyệt (<see cref="IBrowserFile"/>) MỘT CÁCH TRỄ:
/// stream chỉ được mở khi HttpClient thật sự gửi tới phần này, gửi xong thì đóng ngay.
/// <para>
/// Lý do (Blazor Server): mỗi <c>OpenReadStream</c> mở ngay một luồng dữ liệu trình duyệt → máy chủ trên cùng kết nối
/// SignalR của circuit. Nếu mở trước stream của cả nhóm file thì multipart chỉ đọc một stream một lúc, các stream còn lại
/// bị dồn ứ và cả kết nối bị nghẽn; sau 1 phút Blazor ném TimeoutException "Did not receive any data in the allotted time"
/// cho circuit → giao diện đứng rồi hiện "Tải lại trang". Mở trễ thì lúc nào cũng chỉ một file đang truyền.
/// </para>
/// Không đọc cả file vào bộ nhớ: dữ liệu chảy thẳng từ trình duyệt sang API.
/// </summary>
public sealed class BrowserFileContent : HttpContent
{
    private readonly IBrowserFile file;
    private readonly long maxAllowedSize;
    private readonly CancellationToken cancellationToken;

    public BrowserFileContent(IBrowserFile file, long maxAllowedSize, CancellationToken cancellationToken = default)
    {
        this.file = file;
        this.maxAllowedSize = maxAllowedSize;
        this.cancellationToken = cancellationToken;
        Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, cancellationToken);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
        // Mặc định Blazor chỉ cho đọc 500 KB, nên phải truyền maxAllowedSize
        await using var source = file.OpenReadStream(maxAllowedSize, linked.Token);
        await source.CopyToAsync(stream, linked.Token);
    }

    /// <summary>Dung lượng đã biết trước nên request có Content-Length, không phải gửi dạng chunked.</summary>
    protected override bool TryComputeLength(out long length)
    {
        length = file.Size;
        return true;
    }
}
