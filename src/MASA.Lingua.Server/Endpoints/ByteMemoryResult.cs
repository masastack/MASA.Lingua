namespace MASA.Lingua.Server.Endpoints;

/// <summary>
/// 直接把已编译好的 UTF-8 字节块写进响应体。
/// <para>
/// <c>Results.Bytes</c> 只接受 <c>byte[]</c>，用它返回缓存的 <see cref="ReadOnlyMemory{T}"/>
/// 每次请求都要 <c>ToArray()</c> 复制整包 —— 而语言包分发是全站最热的路径。
/// 这里按 <see cref="ReadOnlyMemory{T}"/> 直接写，零拷贝。
/// </para>
/// <para>
/// 刻意**不**设置 <c>Content-Length</c>：响应可能被压缩中间件重新编码，长度由管线决定；
/// 预先写死长度会让压缩后的负载被截断或触发写异常。分块传输的开销相对于压缩收益可忽略。
/// </para>
/// </summary>
internal static class ByteMemoryResult
{
    public static IResult Create(ReadOnlyMemory<byte> payload, string contentType) =>
        new MemoryBodyResult(payload, contentType);

    private sealed class MemoryBodyResult(ReadOnlyMemory<byte> payload, string contentType) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = contentType;
            return httpContext.Response.Body.WriteAsync(payload).AsTask();
        }
    }
}
