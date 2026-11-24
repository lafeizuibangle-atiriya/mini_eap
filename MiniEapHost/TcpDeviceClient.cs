using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiniEapHost
{
    /// <summary>
    /// 设备 TCP 客户端：与设备模拟器之间的 JSON 行协议通道。
    /// 连接后后台线程持续读取事件行，通过事件回调抛给界面线程。
    /// </summary>
    public class TcpDeviceClient : IDisposable
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private CancellationTokenSource _cts;
        private readonly object _sendLock = new object();

        /// <summary>收到一行事件（status / alarm / alarm_clear / run_done / ack）</summary>
        public event Action<string> LineReceived;

        /// <summary>连接断开</summary>
        public event Action<string> Disconnected;

        public bool IsConnected => _client != null && _client.Connected;

        public void Connect(string host, int port)
        {
            if (IsConnected) throw new InvalidOperationException("已连接");
            _client = new TcpClient();
            _client.Connect(host, port);
            _stream = _client.GetStream();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoop(_cts.Token));
        }

        public void Disconnect(string reason = "主动断开")
        {
            try
            {
                _cts?.Cancel();
                _stream?.Close();
                _client?.Close();
            }
            catch { /* 忽略关闭异常 */ }
            _stream = null;
            _client = null;
            Disconnected?.Invoke(reason);
        }

        /// <summary>发送命令（JSON 文本，自动补行分隔符）</summary>
        public void Send(string json)
        {
            if (!IsConnected) throw new InvalidOperationException("未连接设备");
            var data = Encoding.UTF8.GetBytes(json + "\n");
            lock (_sendLock)
            {
                _stream.Write(data, 0, data.Length);
                _stream.Flush();
            }
        }

        private async Task ReadLoop(CancellationToken token)
        {
            var buffer = new byte[4096];
            var sb = new StringBuilder();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int n = await _stream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (n == 0) throw new SocketException((int)SocketError.ConnectionReset);
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    string pending = sb.ToString();
                    int idx;
                    while ((idx = pending.IndexOf('\n')) >= 0)
                    {
                        string line = pending.Substring(0, idx).Trim();
                        pending = pending.Substring(idx + 1);
                        if (line.Length > 0)
                            LineReceived?.Invoke(line);
                    }
                    sb.Clear();
                    sb.Append(pending);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ReadLoop 退出: " + ex.Message);
                Disconnected?.Invoke("连接中断: " + ex.Message);
            }
        }

        public void Dispose() => Disconnect("Dispose");
    }
}
