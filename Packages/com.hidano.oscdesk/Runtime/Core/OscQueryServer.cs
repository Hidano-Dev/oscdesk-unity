// OscQueryServer.cs — OSCQuery のサーバー(HTTP と WebSocket)と mDNS の応答側のソケット(DESIGN.md D-164・HID-490)。
// TcpListener と Socket だけで書く(HttpListener は IL2CPP の削りと URL の予約の都合で使わない。外部 DLL も使わない。D-095)。
// 受け付けと接続ごとの処理は専用のスレッドで行い、名前空間の木の差し替え(Publish)はメインスレッドから呼ぶ。UnityEngine に依存しない。
// 認証は無い。信頼できる LAN の中だけで使う前提(README の「セキュリティ前提」)。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OscDesk.Receiver
{
    /// <summary>OscQueryServer の設定。</summary>
    public sealed class OscQueryServerOptions
    {
        /// <summary>HOST_INFO の NAME と mDNS のインスタンス名(製品名とポート。<see cref="OscMdnsService.MakeInstanceName"/>)。</summary>
        public string Name = "OscDesk";

        /// <summary>HTTP と WebSocket の TCP ポート。0 なら OS が空きを選ぶ(mDNS で告知するので固定しなくてよい)。</summary>
        public int HttpPort;

        /// <summary>OSC を受ける UDP ポート(HOST_INFO の OSC_PORT)。</summary>
        public int OscPort;

        /// <summary>mDNS で _oscjson._tcp を告知・応答するか。</summary>
        public bool AdvertiseMdns = true;

        /// <summary>SRV のホスト名の元(空なら Environment.MachineName)。</summary>
        public string MachineName;

        /// <summary>起動ごとの ID を返す(既定は <see cref="OscBootId.Current"/>)。どのスレッドから呼ばれてもよいこと。</summary>
        public Func<string> BootId;
    }

    /// <summary>
    /// OSCQuery のサーバー。作った時点で TCP の待ち受けを始める(開けなければ SocketException)。mDNS は失敗しても止めず、
    /// 理由を <see cref="MdnsError"/> に残す(Windows のファイアウォールや 5353 の共有の失敗で HTTP まで止めないため)。
    /// </summary>
    public sealed class OscQueryServer : IDisposable
    {
        /// <summary>同時に受ける接続の上限(HTTP と WebSocket の合計)。</summary>
        public const int MaxConnections = 32;

        /// <summary>WebSocket のクライアント 1 つに溜められる送信の数。超えたら(相手が読まない)その接続を閉じる。</summary>
        public const int MaxQueuedFramesPerClient = 256;

        /// <summary>HTTP の要求を読み切るまでの待ち時間(ミリ秒)。</summary>
        public const int HttpReadTimeoutMilliseconds = 5000;

        private const int SendTimeoutMilliseconds = 2000;

        private readonly TcpListener listener;
        private readonly Thread acceptThread;
        private readonly OscQueryServerOptions options;
        private readonly Func<string> bootId;
        private readonly List<WebSocketClient> clients = new List<WebSocketClient>();
        private readonly object clientsGate = new object();
        private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
        private readonly MdnsResponder mdns;
        private volatile OscQueryTree tree = OscQueryTree.Empty();
        private volatile bool closed;
        private int connections;

        public OscQueryServer(OscQueryServerOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            if (options.HttpPort < 0 || options.HttpPort > 65535) throw new ArgumentOutOfRangeException(nameof(options), "HttpPort");
            bootId = options.BootId ?? (() => OscBootId.Current);
            listener = new TcpListener(IPAddress.Any, options.HttpPort);
            listener.Start(MaxConnections);
            HttpPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "OscDesk OSCQuery accept" };
            acceptThread.Start();

            if (options.AdvertiseMdns)
            {
                try
                {
                    var service = new OscMdnsService(options.Name,
                        OscMdnsService.MakeHostLabel(string.IsNullOrEmpty(options.MachineName) ? Environment.MachineName : options.MachineName, options.OscPort),
                        HttpPort, OscNetwork.LocalIPv4Addresses());
                    mdns = new MdnsResponder(service, messages);
                    Service = service;
                }
                catch (Exception exception) when (exception is SocketException || exception is NetworkInformationException
                    || exception is InvalidOperationException || exception is NotSupportedException)
                {
                    MdnsError = exception.Message;
                }
            }
        }

        /// <summary>実際に待ち受けている TCP ポート(HOST_INFO の WS_PORT と同じ)。</summary>
        public int HttpPort { get; }

        /// <summary>mDNS を始められなかった理由。始められたか、告知しない設定なら null。</summary>
        public string MdnsError { get; }

        /// <summary>告知しているサービス(mDNS を始められなかったら null)。</summary>
        public OscMdnsService Service { get; }

        /// <summary>今の名前空間の木。</summary>
        public OscQueryTree Tree => tree;

        public int WebSocketClientCount
        {
            get
            {
                lock (clientsGate)
                {
                    return clients.Count;
                }
            }
        }

        /// <summary>今の HOST_INFO の JSON。</summary>
        public string HostInfoJson()
        {
            return OscQueryHostInfo.ToJson(options.Name, options.OscPort, HttpPort, bootId());
        }

        /// <summary>
        /// 受信スレッドで起きたこと(mDNS の送信の失敗など)。メインスレッドで取り出してログに出す。溜まりすぎないよう 64 件で古いものを捨てる。
        /// </summary>
        public bool TryTakeMessage(out string message)
        {
            return messages.TryDequeue(out message);
        }

        /// <summary>
        /// 名前空間の木を差し替え、前の木との違いを WebSocket のクライアントへ PATH_* で知らせる。知らせた変化を返す。
        /// </summary>
        public List<OscQueryPathChange> Publish(OscQueryTree next)
        {
            next = next ?? OscQueryTree.Empty();
            var changes = OscQueryTree.Diff(tree, next);
            tree = next;
            if (changes.Count == 0)
            {
                return changes;
            }

            var frames = new List<byte[]>(changes.Count);
            foreach (var change in changes)
            {
                frames.Add(OscWebSocket.EncodeText(change.ToJson()));
            }

            WebSocketClient[] snapshot;
            lock (clientsGate)
            {
                snapshot = clients.ToArray();
            }

            foreach (var client in snapshot)
            {
                foreach (var frame in frames)
                {
                    if (!client.Enqueue(frame))
                    {
                        Note("OSCQuery: WebSocket のクライアント " + client.Remote + " が読まないので閉じました。");
                        client.Close();
                        break;
                    }
                }
            }

            return changes;
        }

        public void Dispose()
        {
            if (closed)
            {
                return;
            }

            closed = true;
            mdns?.Dispose();
            try
            {
                listener.Stop();
            }
            catch (SocketException)
            {
            }

            WebSocketClient[] snapshot;
            lock (clientsGate)
            {
                snapshot = clients.ToArray();
                clients.Clear();
            }

            foreach (var client in snapshot)
            {
                client.Close();
            }

            acceptThread.Join(1000);
        }

        private void Note(string message)
        {
            messages.Enqueue(message);
            while (messages.Count > 64 && messages.TryDequeue(out _))
            {
            }
        }

        private void AcceptLoop()
        {
            while (!closed)
            {
                Socket socket;
                try
                {
                    socket = listener.AcceptSocket();
                }
                catch (SocketException)
                {
                    if (closed) return;
                    Thread.Sleep(50);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return; // Stop の後
                }

                if (Interlocked.Increment(ref connections) > MaxConnections)
                {
                    Interlocked.Decrement(ref connections);
                    CloseQuietly(socket);
                    continue;
                }

                var thread = new Thread(() => Serve(socket)) { IsBackground = true, Name = "OscDesk OSCQuery connection" };
                thread.Start();
            }
        }

        private void Serve(Socket socket)
        {
            try
            {
                socket.NoDelay = true;
                socket.ReceiveTimeout = HttpReadTimeoutMilliseconds;
                socket.SendTimeout = SendTimeoutMilliseconds;
                var buffer = new byte[OscHttp.MaxHeaderBytes];
                var count = 0;
                OscHttpRequest request;
                int consumed;
                while (true)
                {
                    var read = socket.Receive(buffer, count, buffer.Length - count, SocketFlags.None);
                    if (read <= 0)
                    {
                        return;
                    }

                    count += read;
                    var result = OscHttp.TryParse(buffer, count, out request, out consumed);
                    if (result == OscHttpParseResult.Ok)
                    {
                        break;
                    }

                    if (result == OscHttpParseResult.Bad)
                    {
                        SendAll(socket, new OscHttpResponse(400, "Bad Request").ToBytes());
                        return;
                    }

                    if (result == OscHttpParseResult.TooLarge)
                    {
                        SendAll(socket, new OscHttpResponse(431, "Request Header Fields Too Large").ToBytes());
                        return;
                    }
                }

                if (OscWebSocket.IsUpgrade(request))
                {
                    var handshake = OscWebSocket.BuildHandshake(request, out var accepted);
                    SendAll(socket, handshake);
                    if (accepted)
                    {
                        ServeWebSocket(socket, buffer, consumed, count - consumed);
                    }

                    return;
                }

                OscHttpResponse response;
                try
                {
                    response = OscHttp.Respond(request, tree, HostInfoJson());
                }
                catch (Exception exception)
                {
                    Note("OSCQuery: 要求 " + request.Path + " に答えられませんでした: " + exception.Message);
                    response = new OscHttpResponse(500, "Internal Server Error");
                }

                SendAll(socket, response.ToBytes(request.Method != "HEAD"));
                socket.Shutdown(SocketShutdown.Send);
            }
            catch (SocketException)
            {
                // 相手が切った・時間切れ。黙って閉じる
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                CloseQuietly(socket);
                Interlocked.Decrement(ref connections);
            }
        }

        private void ServeWebSocket(Socket socket, byte[] headerBuffer, int start, int pending)
        {
            var client = new WebSocketClient(socket);
            lock (clientsGate)
            {
                if (closed)
                {
                    return;
                }

                clients.Add(client);
            }

            try
            {
                // 命令は待つだけなので、読み取りの時間切れは無くす(PATH_* を待つ接続は長く開いたまま)
                socket.ReceiveTimeout = 0;
                var buffer = new byte[OscWebSocket.MaxClientPayloadBytes + 16];
                var count = Math.Min(pending, buffer.Length);
                Buffer.BlockCopy(headerBuffer, start, buffer, 0, count);
                var fragments = 0;
                while (!closed && !client.IsClosed)
                {
                    var offset = 0;
                    while (true)
                    {
                        var result = OscWebSocket.TryReadFrame(buffer, offset, count - offset, out var frame, out var used);
                        if (result == OscWebSocketParseResult.Incomplete)
                        {
                            break;
                        }

                        if (result != OscWebSocketParseResult.Ok)
                        {
                            client.SendNow(OscWebSocket.EncodeClose(result == OscWebSocketParseResult.TooLarge ? 1009 : 1002));
                            return;
                        }

                        offset += used;
                        switch (frame.Opcode)
                        {
                            case OscWebSocketOpcode.Close:
                                client.SendNow(OscWebSocket.EncodeFrame(OscWebSocketOpcode.Close, frame.Payload.Length >= 2
                                    ? new[] { frame.Payload[0], frame.Payload[1] }
                                    : Array.Empty<byte>()));
                                return;
                            case OscWebSocketOpcode.Ping:
                                client.Enqueue(OscWebSocket.EncodeFrame(OscWebSocketOpcode.Pong, frame.Payload));
                                break;
                            case OscWebSocketOpcode.Text:
                            case OscWebSocketOpcode.Binary:
                            case OscWebSocketOpcode.Continuation:
                                // LISTEN / IGNORE は受け付けるだけ(値を送り返す機能(VALUE の配信)は持たない)。分割された命令は数だけ見る
                                fragments = frame.Fin ? 0 : fragments + 1;
                                if (fragments > 64)
                                {
                                    client.SendNow(OscWebSocket.EncodeClose(1009));
                                    return;
                                }

                                break;
                        }
                    }

                    if (offset > 0)
                    {
                        Buffer.BlockCopy(buffer, offset, buffer, 0, count - offset);
                        count -= offset;
                    }

                    if (count == buffer.Length)
                    {
                        client.SendNow(OscWebSocket.EncodeClose(1009));
                        return;
                    }

                    var read = socket.Receive(buffer, count, buffer.Length - count, SocketFlags.None);
                    if (read <= 0)
                    {
                        return;
                    }

                    count += read;
                }
            }
            finally
            {
                lock (clientsGate)
                {
                    clients.Remove(client);
                }

                client.Close();
            }
        }

        private static void SendAll(Socket socket, byte[] bytes)
        {
            var sent = 0;
            while (sent < bytes.Length)
            {
                var n = socket.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
                if (n <= 0)
                {
                    return;
                }

                sent += n;
            }
        }

        private static void CloseQuietly(Socket socket)
        {
            try
            {
                socket.Close();
            }
            catch (Exception)
            {
                // 閉じる途中の失敗は無視する
            }
        }

        // WebSocket のクライアント 1 つ。送信は専用のスレッドが列から取り出して書く(メインスレッドの Publish を遅い相手で止めない)
        private sealed class WebSocketClient
        {
            private readonly Socket socket;
            private readonly BlockingCollection<byte[]> queue = new BlockingCollection<byte[]>(MaxQueuedFramesPerClient);
            private readonly Thread writer;
            private readonly object sendGate = new object();
            private volatile bool isClosed;

            public WebSocketClient(Socket socket)
            {
                this.socket = socket;
                Remote = SafeRemote(socket);
                writer = new Thread(WriteLoop) { IsBackground = true, Name = "OscDesk OSCQuery websocket" };
                writer.Start();
            }

            public string Remote { get; }

            public bool IsClosed => isClosed;

            public bool Enqueue(byte[] frame)
            {
                if (isClosed)
                {
                    return true;
                }

                try
                {
                    return queue.TryAdd(frame);
                }
                catch (InvalidOperationException)
                {
                    return true; // 閉じた後
                }
            }

            public void SendNow(byte[] frame)
            {
                try
                {
                    lock (sendGate)
                    {
                        SendAll(socket, frame);
                    }
                }
                catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException)
                {
                }
            }

            public void Close()
            {
                if (isClosed)
                {
                    return;
                }

                isClosed = true;
                queue.CompleteAdding();
                CloseQuietly(socket);
            }

            private void WriteLoop()
            {
                try
                {
                    foreach (var frame in queue.GetConsumingEnumerable())
                    {
                        lock (sendGate)
                        {
                            SendAll(socket, frame);
                        }
                    }
                }
                catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException || exception is InvalidOperationException)
                {
                    Close();
                }
            }

            private static string SafeRemote(Socket socket)
            {
                try
                {
                    return socket.RemoteEndPoint?.ToString() ?? "?";
                }
                catch (Exception)
                {
                    return "?";
                }
            }
        }

        // mDNS の応答側。5353 は Windows の Dnscache なども使うので ReuseAddress で共有する(HID-484 §1)
        private sealed class MdnsResponder : IDisposable
        {
            private static readonly double[] AnnounceDelaysSeconds = { 0, 1 };
            private readonly OscMdnsService service;
            private readonly ConcurrentQueue<string> messages;
            private readonly Socket socket;
            private readonly Thread thread;
            private readonly List<IPAddress> interfaces = new List<IPAddress>();
            private volatile bool closed;
            private bool sendFailureNoted;

            public MdnsResponder(OscMdnsService service, ConcurrentQueue<string> messages)
            {
                this.service = service;
                this.messages = messages;
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    socket.ExclusiveAddressUse = false;
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    socket.Bind(new IPEndPoint(IPAddress.Any, OscMdns.Port));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                    foreach (var address in service.Addresses)
                    {
                        try
                        {
                            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(OscMdns.MulticastAddress, address));
                            interfaces.Add(address);
                        }
                        catch (SocketException)
                        {
                            // この NIC ではマルチキャストに入れない(仮想 NIC など)。ほかの NIC で続ける
                        }
                    }

                    if (interfaces.Count == 0)
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(OscMdns.MulticastAddress));
                    }

                    socket.ReceiveTimeout = 250;
                }
                catch
                {
                    socket.Close();
                    throw;
                }

                thread = new Thread(Loop) { IsBackground = true, Name = "OscDesk mDNS" };
                thread.Start();
            }

            public void Dispose()
            {
                if (closed)
                {
                    return;
                }

                closed = true;
                thread.Join(1000);
                try
                {
                    // 取り下げ(TTL 0)。届かなくても相手のキャッシュは TTL で消える
                    SendMulticast(OscMdns.BuildAnnouncement(service, 0));
                }
                catch (Exception)
                {
                }

                socket.Close();
            }

            private void Loop()
            {
                var buffer = new byte[9000];
                var started = DateTime.UtcNow;
                var announced = 0;
                while (!closed)
                {
                    if (announced < AnnounceDelaysSeconds.Length && (DateTime.UtcNow - started).TotalSeconds >= AnnounceDelaysSeconds[announced])
                    {
                        announced += 1;
                        SendMulticast(OscMdns.BuildAnnouncement(service));
                    }

                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int length;
                    try
                    {
                        length = socket.ReceiveFrom(buffer, ref remote);
                    }
                    catch (SocketException exception)
                    {
                        if (exception.SocketErrorCode == SocketError.TimedOut || exception.SocketErrorCode == SocketError.ConnectionReset
                            || exception.SocketErrorCode == SocketError.MessageSize)
                        {
                            continue;
                        }

                        if (closed) return;
                        Note("mDNS: 受信できませんでした: " + exception.Message);
                        Thread.Sleep(250);
                        continue;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    if (!OscMdns.TryParse(buffer, length, out var message) || message.IsResponse)
                    {
                        continue;
                    }

                    var from = (IPEndPoint)remote;
                    var legacy = from.Port != OscMdns.Port;
                    var response = OscMdns.BuildResponse(service, message, legacy);
                    if (response == null)
                    {
                        continue;
                    }

                    var unicast = legacy;
                    foreach (var question in message.Questions)
                    {
                        unicast |= question.UnicastResponse;
                    }

                    try
                    {
                        if (unicast)
                        {
                            socket.SendTo(response, from);
                        }
                        else
                        {
                            SendMulticast(response);
                        }
                    }
                    catch (SocketException exception)
                    {
                        NoteSendFailure(exception);
                    }
                }
            }

            private void SendMulticast(byte[] packet)
            {
                var target = new IPEndPoint(OscMdns.MulticastAddress, OscMdns.Port);
                if (interfaces.Count == 0)
                {
                    TrySend(packet, target);
                    return;
                }

                // NIC ごとに出す(既定の NIC だけに出すと、別の NIC の側にいるブリッジに届かない)
                foreach (var address in interfaces)
                {
                    try
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    }
                    catch (SocketException)
                    {
                        continue;
                    }

                    TrySend(packet, target);
                }
            }

            private void TrySend(byte[] packet, IPEndPoint target)
            {
                try
                {
                    socket.SendTo(packet, target);
                    sendFailureNoted = false;
                }
                catch (SocketException exception)
                {
                    NoteSendFailure(exception);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            private void NoteSendFailure(SocketException exception)
            {
                if (sendFailureNoted)
                {
                    return;
                }

                sendFailureNoted = true;
                Note("mDNS: 送れませんでした: " + exception.Message);
            }

            private void Note(string message)
            {
                messages.Enqueue(message);
                while (messages.Count > 64 && messages.TryDequeue(out _))
                {
                }
            }
        }
    }

    /// <summary>この PC の IPv4 のアドレス。</summary>
    public static class OscNetwork
    {
        /// <summary>動いている NIC の IPv4 のユニキャストのアドレス(ループバックを除く)。引けなければホスト名から引く。</summary>
        public static List<IPAddress> LocalIPv4Addresses()
        {
            var result = new List<IPAddress>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        var address = unicast.Address;
                        if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !result.Contains(address))
                        {
                            result.Add(address);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is NetworkInformationException || exception is NotSupportedException
                || exception is PlatformNotSupportedException)
            {
                // 下のホスト名から引く方へ
            }

            if (result.Count == 0)
            {
                try
                {
                    foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
                    {
                        if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !result.Contains(address))
                        {
                            result.Add(address);
                        }
                    }
                }
                catch (SocketException)
                {
                }
            }

            return result;
        }
    }
}
