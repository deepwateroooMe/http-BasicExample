using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using IocpSharp.Http.Responsers;
using IocpSharp.Http.Streams;
using System.IO.Compression;
using IocpSharp.Server;
using IocpSharp.Http.Utils;
using IocpSharp.WebSocket;
namespace IocpSharp.Http {
    // 我们独立出一个基类来，以后新的服务继承本类就好
    public class HttpServerBase : TcpIocpServer { // 服务器：它只有一个，单线程（多进程？）

        // 还是说：这里我理解错了,不是说一个服务器同时在线只能连20个客户端,而是一个客户端的请求,最多分 20个线程来异步"断点续传"一样的把大文件高效上传或是下载? （一个cookie: 最多使用  20  次）  去消化一下这个知识点
        private static int MaxRequestPerConnection = 20; 
// 还是说，客户端的缓存请求队列里，一次可以同时发送20个请求，等待服务器端来串行处理呢？
        
        private string _webRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web"));
        private static string _uplaodTempDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uploads"));
        public string WebRoot { get => _webRoot; set => _webRoot = value; }
        public static string UplaodTempDir { get => _uplaodTempDir; set => _uplaodTempDir = value; }

        // 后面的代码可能会越来越复杂，我们做个简单的路由功能
        // 可以开发功能更强大的路由
        // 单线程：就不涉及线程安全的问题。注册管理的是回调的类型，至于到时回调的实例管理，可以去找看一下
        private Dictionary<string, Func<HttpRequest, Stream, bool>> _routes = new Dictionary<string, Func<HttpRequest, Stream, bool>>();

        public HttpServerBase() : base() { }

        protected override void Start() {
            if (!Directory.Exists(_webRoot)) throw new Exception($"网站根目录不存在，请手动创建：{_webRoot}");
            base.Start(); // <<<<<<<<<<<<<<<<<<<< 底层是异步封装实现的
        }

        public void RegisterRoute(string path, Func<HttpRequest, Stream, bool> route) {
            _routes[path] = route;
        }

        protected override void NewClient(Socket client) {
            Stream stream = new BufferedNetworkStream(client, true);
            // 设置每个链接能处理的请求数
            int processedRequest = 0;
            while (processedRequest < MaxRequestPerConnection) {
                HttpRequest request = null;
                try {
                    // 捕获一个HttpRequest
                    request = HttpRequest.Capture(stream);
                    // 如果是WebSocket，调用相应的处理方法
                    if (request.IsWebSocket) {
                        if(!OnWebSocketInternal(request, stream)) {
                            // WebSocket处理异常，关闭基础流
                            stream.Close();
                        }
                        return;
                    }
                    // 尝试查找路由，不存在的话使用NotFound路由
                    if (!_routes.TryGetValue(request.Path, out Func<HttpRequest, Stream, bool> handler)) {
                        // 未匹配到路由，统一当文件资源处理
                        handler = OnResource;
                    }
                    // 如果处理程序返回false，那么我们退出循环，关掉连接。
                    if (!handler(request, stream)) break;
                    // 确保当前请求的请求实体读取完毕
                    request.EnsureEntityBodyRead();
                    // 释放掉当前请求，准备下一次请求
                    processedRequest++;
                }
                catch (HttpRequestException e) {
                    if (e.Error == HttpRequestError.ConnectionLost) break;
                    // 客户端发送的请求异常
                    OnBadRequest(stream, $"请求异常：{e.Error}");
                    break;
                }
                catch (Exception e) {
                    Console.WriteLine(e.ToString());
                    // 其他异常
                    OnServerError(stream, $"请求异常：{e}");
                    break;
                }
                finally {
                    // 始终释放请求
                    request?.Dispose();
                }
            }
            stream.Close();
        }
        // 响应404错误
        // <param name="request"></param>
        // <param name="stream"></param>
        protected virtual bool OnNotFound(HttpRequest request, Stream stream) {
            HttpResponser responser = new ChunkedResponser(404);
            responser.KeepAlive = false;
            responser.ContentType = "text/html; charset=utf-8";
            responser.Write(stream, $"请求的资源'{request.Path}'不存在。");
            responser.End(stream);
            return false;
        }
        // 请求异常
        // <param name="request"></param>
        // <param name="stream"></param>
        // <param name="message"></param>
        protected virtual void OnBadRequest(Stream stream, string message) {
            HttpResponser responser = new ChunkedResponser(400);
            responser.KeepAlive = false;
            responser.ContentType = "text/html; charset=utf-8";
            responser.Write(stream, message);
            responser.End(stream);
        }
        // 服务器异常
        // <param name="request"></param>
        // <param name="stream"></param>
        // <param name="message"></param>
        protected virtual void OnServerError(Stream stream, string message) {
            HttpResponser responser = new ChunkedResponser(500);
            responser.KeepAlive = false;
            responser.ContentType = "text/html; charset=utf-8";
            responser.Write(stream, message);
            responser.End(stream);
        }
        // 发送服务器资源，这里简单处理下。
        // 必要的情况下可以作缓存处理
        // <param name="request"></param>
        // <param name="stream"></param>
        protected virtual bool OnResource(HttpRequest request, Stream stream) {
            string path = request.Path;
            // 处理下非安全的路径
            if (path.IndexOf("..") >= 0 || !path.StartsWith("/")) {
                throw new HttpRequestException(HttpRequestError.ResourcePathError, "不安全的路径访问");
            }
            string filePath = Path.GetFullPath(Path.Combine(_webRoot, "." + path));
            FileInfo fileInfo = new FileInfo(filePath);
            string mimeType = MimeTypes.GetMimeType(fileInfo.Extension);
            if (string.IsNullOrEmpty(mimeType)) {
                throw new HttpRequestException(HttpRequestError.ResourceMimeError, "不支持的文件类型");
            }
            if (!fileInfo.Exists) {
                return OnNotFound(request, stream);
            }
            HttpResponser responser = new HttpResponser();
            // 拿到的MIME输出给客户端
            responser.ContentType = mimeType;
            responser.ContentLength = fileInfo.Length;
            using (Stream output = responser.OpenWrite(stream)) {
                using(Stream input = fileInfo.OpenRead()) {
                    input.CopyTo(stream);
                }
            }
            return true;
        }
        // 处理WebSocket
        // <param name="request"></param>
        // <param name="stream"></param>
        private bool OnWebSocketInternal(HttpRequest request, Stream stream) {
            string webSocketKey = request.Headers["Sec-WebSocket-Key"];
            if(string.IsNullOrEmpty(webSocketKey)) {
                OnBadRequest(stream, "header 'Sec-WebSocket-Key' error");
                return false;
            }
            // 获取客户端发送来的Sec-WebSocket-Key字节数组
            byte[] keyBytes = Encoding.ASCII.GetBytes(webSocketKey);
            // 拼接上WebSocket的Salt，固定值：258EAFA5-E914-47DA-95CA-C5AB0DC85B11
            keyBytes = keyBytes.Concat(ProtocolUtils.Salt).ToArray();
            // 计算HASH值，作为响应给客户端的Sec-WebSocket-Accept
            string secWebSocketAcceptKey = ProtocolUtils.SHA1(keyBytes);
            // 响应101状态码给客户端
            HttpResponser responser = new HttpResponser(101);
            responser["Upgrade"] = "websocket";
            responser["Connection"] = "Upgrade";
            // 设置Sec-WebSocket-Accept头
            responser["Sec-WebSocket-Accept"] = secWebSocketAcceptKey;
            // 发送响应
            responser.WriteHeader(stream);
           
            // 开始WebSocket消息的接收和发送
            OnWebSocket(request, stream);
            return true;
        }
        // WebSocket消息处理程序
        // <param name="request"></param>
        // <param name="stream"></param>
        protected virtual void OnWebSocket(HttpRequest request, Stream stream) {
            stream.Close();
        }
    }
}
