// OscQueryHttp.cs — OSCQuery の HTTP の要求の読み取り・応答の組み立てと、WebSocket の握手とフレーム(HID-490)。
// ソケットは触らない(OscQueryServer が持つ)。HttpListener を使わないのは IL2CPP の削り(stripping)と URL の予約を避けるため。
// UnityEngine に依存しない。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OscDesk.Receiver
{
    /// <summary>HTTP の要求 1 件(要求行とヘッダー。本文は読まない)。</summary>
    public sealed class OscHttpRequest
    {
        public string Method { get; }

        /// <summary>パーセントエンコードを解いたパス(クエリを除く)。</summary>
        public string Path { get; }

        /// <summary>? の後ろ(解いたもの)。無ければ空。</summary>
        public string Query { get; }

        public IReadOnlyDictionary<string, string> Headers { get; }

        public OscHttpRequest(string method, string path, string query, IReadOnlyDictionary<string, string> headers)
        {
            Method = method ?? string.Empty;
            Path = path ?? "/";
            Query = query ?? string.Empty;
            Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string Header(string name)
        {
            return Headers.TryGetValue(name, out var value) ? value : null;
        }
    }

    public enum OscHttpParseResult
    {
        /// <summary>ヘッダーの終わり(空行)がまだ届いていない。</summary>
        Incomplete,
        Ok,
        /// <summary>読めない要求。400 を返して閉じる。</summary>
        Bad,
        /// <summary>ヘッダーが上限より大きい。431 を返して閉じる。</summary>
        TooLarge
    }

    /// <summary>HTTP の応答 1 件。</summary>
    public sealed class OscHttpResponse
    {
        public int Status { get; }
        public string Reason { get; }
        public string ContentType { get; }
        public byte[] Body { get; }

        public OscHttpResponse(int status, string reason, string contentType = null, byte[] body = null)
        {
            Status = status;
            Reason = reason ?? string.Empty;
            ContentType = contentType;
            Body = body ?? Array.Empty<byte>();
        }

        public static OscHttpResponse Json(string json)
        {
            return new OscHttpResponse(200, "OK", "application/json; charset=utf-8", OscHttp.Utf8.GetBytes(json));
        }

        /// <summary>ヘッダーと本文のバイト列。1 要求ごとに閉じる(Connection: close)。HEAD なら本文を付けない。</summary>
        public byte[] ToBytes(bool includeBody = true)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason).Append("\r\n");
            if (ContentType != null)
            {
                head.Append("Content-Type: ").Append(ContentType).Append("\r\n");
            }

            // 204 は本文の長さを書かない(RFC 9110)
            if (Status != 204)
            {
                head.Append("Content-Length: ").Append(Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }

            // ブラウザで名前空間を開いて確かめられるように(認証は無い。信頼できる LAN だけで使う前提)
            head.Append("Access-Control-Allow-Origin: *\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Connection: close\r\n\r\n");
            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            if (!includeBody || Body.Length == 0 || Status == 204)
            {
                return headBytes;
            }

            var all = new byte[headBytes.Length + Body.Length];
            Buffer.BlockCopy(headBytes, 0, all, 0, headBytes.Length);
            Buffer.BlockCopy(Body, 0, all, headBytes.Length, Body.Length);
            return all;
        }
    }

    /// <summary>HTTP の要求の読み取りと、OSCQuery の要求への答え。</summary>
    public static class OscHttp
    {
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>要求行とヘッダーの大きさの上限。</summary>
        public const int MaxHeaderBytes = 16 * 1024;

        /// <summary>
        /// buffer[0..count) から要求行とヘッダーを読む。Ok なら consumed は空行の次の位置。
        /// 本文(Content-Length)は扱わない(OSCQuery の要求は GET だけ)。
        /// </summary>
        public static OscHttpParseResult TryParse(byte[] buffer, int count, out OscHttpRequest request, out int consumed)
        {
            request = null;
            consumed = 0;
            var end = -1;
            for (var i = 3; i < count; i++)
            {
                if (buffer[i] == '\n' && buffer[i - 1] == '\r' && buffer[i - 2] == '\n' && buffer[i - 3] == '\r')
                {
                    end = i + 1;
                    break;
                }
            }

            if (end < 0)
            {
                return count >= MaxHeaderBytes ? OscHttpParseResult.TooLarge : OscHttpParseResult.Incomplete;
            }

            if (end > MaxHeaderBytes)
            {
                return OscHttpParseResult.TooLarge;
            }

            // ヘッダーは 1 バイト 1 文字で読む(ISO-8859-1 と同じ)。Encoding.GetEncoding(28591) は IL2CPP で削られ得るので使わない
            var chars = new char[end - 4];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)buffer[i];
            }

            var text = new string(chars);
            var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || requestLine[0].Length == 0 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            {
                return OscHttpParseResult.Bad;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0)
                {
                    return OscHttpParseResult.Bad;
                }

                var name = lines[i].Substring(0, colon).Trim();
                var value = lines[i].Substring(colon + 1).Trim();
                headers[name] = headers.TryGetValue(name, out var previous) ? previous + ", " + value : value;
            }

            var target = requestLine[1];
            // 絶対形式(http://host/path)も受ける
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                var slash = target.IndexOf('/', 7);
                target = slash < 0 ? "/" : target.Substring(slash);
            }

            if (target.Length == 0 || target[0] != '/')
            {
                return OscHttpParseResult.Bad;
            }

            var question = target.IndexOf('?');
            var rawPath = question < 0 ? target : target.Substring(0, question);
            var rawQuery = question < 0 ? string.Empty : target.Substring(question + 1);
            if (!TryPercentDecode(rawPath, out var path) || !TryPercentDecode(rawQuery, out var query))
            {
                return OscHttpParseResult.Bad;
            }

            request = new OscHttpRequest(requestLine[0], path, query, headers);
            consumed = end;
            return OscHttpParseResult.Ok;
        }

        /// <summary>%XX を解く(UTF-8 として読めなければ false)。+ は空白にしない(パスに + が来てよい)。</summary>
        public static bool TryPercentDecode(string text, out string decoded)
        {
            decoded = text ?? string.Empty;
            if (decoded.IndexOf('%') < 0)
            {
                return true;
            }

            var bytes = new List<byte>(decoded.Length);
            for (var i = 0; i < decoded.Length; i++)
            {
                var c = decoded[i];
                if (c == '%')
                {
                    if (i + 2 >= decoded.Length
                        || !byte.TryParse(decoded.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    {
                        return false;
                    }

                    bytes.Add(b);
                    i += 2;
                }
                else if (c < 0x80)
                {
                    bytes.Add((byte)c);
                }
                else
                {
                    return false;
                }
            }

            try
            {
                decoded = StrictUtf8.GetString(bytes.ToArray());
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// OSCQuery の要求に答える。<c>?HOST_INFO</c> は HOST_INFO、クエリなしはノードと子孫、<c>?属性</c> はその属性だけ。
        /// 対応していない属性・ノードが持たない属性は 204(HID-484 §6 の 1)、無いパスは 404、GET / HEAD 以外は 405。
        /// </summary>
        public static OscHttpResponse Respond(OscHttpRequest request, OscQueryTree tree, string hostInfoJson)
        {
            if (request.Method != "GET" && request.Method != "HEAD")
            {
                return new OscHttpResponse(405, "Method Not Allowed");
            }

            var query = request.Query;
            // ?HOST_INFO&foo のような余計な続きは無視する
            var amp = query.IndexOf('&');
            if (amp >= 0)
            {
                query = query.Substring(0, amp);
            }

            var equals = query.IndexOf('=');
            if (equals >= 0)
            {
                query = query.Substring(0, equals);
            }

            if (query == "HOST_INFO")
            {
                return OscHttpResponse.Json(hostInfoJson ?? "{}");
            }

            if (tree == null || !tree.TryFind(request.Path, out var node))
            {
                return new OscHttpResponse(404, "Not Found", "text/plain; charset=utf-8", Utf8.GetBytes("No such path"));
            }

            if (query.Length == 0)
            {
                return OscHttpResponse.Json(tree.ToJson(node));
            }

            var attribute = OscQueryTree.AttributeJson(node, query);
            return attribute == null ? new OscHttpResponse(204, "No Content") : OscHttpResponse.Json(attribute);
        }
    }

    public enum OscWebSocketOpcode
    {
        Continuation = 0x0,
        Text = 0x1,
        Binary = 0x2,
        Close = 0x8,
        Ping = 0x9,
        Pong = 0xA
    }

    /// <summary>WebSocket のフレーム 1 つ(マスクを解いた中身)。</summary>
    public readonly struct OscWebSocketFrame
    {
        public bool Fin { get; }
        public OscWebSocketOpcode Opcode { get; }
        public byte[] Payload { get; }

        public OscWebSocketFrame(bool fin, OscWebSocketOpcode opcode, byte[] payload)
        {
            Fin = fin;
            Opcode = opcode;
            Payload = payload ?? Array.Empty<byte>();
        }
    }

    public enum OscWebSocketParseResult
    {
        Incomplete,
        Ok,
        /// <summary>規約違反(マスクの無いクライアントのフレーム・予約ビット・大きすぎる中身)。1002 / 1009 で閉じる。</summary>
        ProtocolError,
        TooLarge
    }

    /// <summary>WebSocket(RFC 6455)の握手とフレーム。拡張(圧縮)は受けない。</summary>
    public static class OscWebSocket
    {
        private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        /// <summary>クライアントから受けるフレームの中身の上限(OSCQuery の命令は短い JSON)。</summary>
        public const int MaxClientPayloadBytes = 64 * 1024;

        /// <summary>WebSocket への切り替えの要求か(GET・Upgrade: websocket・Connection に upgrade・鍵あり)。</summary>
        public static bool IsUpgrade(OscHttpRequest request)
        {
            if (request == null || request.Method != "GET")
            {
                return false;
            }

            var upgrade = request.Header("Upgrade");
            var connection = request.Header("Connection");
            return upgrade != null && upgrade.Trim().Equals("websocket", StringComparison.OrdinalIgnoreCase)
                && connection != null && connection.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) >= 0
                && !string.IsNullOrEmpty(request.Header("Sec-WebSocket-Key"));
        }

        public static string ComputeAccept(string key)
        {
            using (var sha1 = SHA1.Create())
            {
                var hash = sha1.ComputeHash(Encoding.ASCII.GetBytes((key ?? string.Empty).Trim() + AcceptGuid));
                return Convert.ToBase64String(hash);
            }
        }

        /// <summary>101 Switching Protocols の応答。版が 13 でなければ 426 を返す(呼び出し側は閉じる)。</summary>
        public static byte[] BuildHandshake(OscHttpRequest request, out bool accepted)
        {
            var version = request.Header("Sec-WebSocket-Version");
            if (version != null && version.Trim() != "13")
            {
                accepted = false;
                return Encoding.ASCII.GetBytes("HTTP/1.1 426 Upgrade Required\r\nSec-WebSocket-Version: 13\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            }

            accepted = true;
            return Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "
                + ComputeAccept(request.Header("Sec-WebSocket-Key")) + "\r\n\r\n");
        }

        /// <summary>
        /// buffer[offset..offset+count) からクライアントのフレームを 1 つ読む。クライアントのフレームはマスク必須(RFC 6455 §5.1)。
        /// </summary>
        public static OscWebSocketParseResult TryReadFrame(byte[] buffer, int offset, int count, out OscWebSocketFrame frame, out int consumed,
            int maxPayload = MaxClientPayloadBytes)
        {
            frame = default;
            consumed = 0;
            if (count < 2)
            {
                return OscWebSocketParseResult.Incomplete;
            }

            var b0 = buffer[offset];
            var b1 = buffer[offset + 1];
            if ((b0 & 0x70) != 0)
            {
                return OscWebSocketParseResult.ProtocolError; // 拡張を合意していないので予約ビットは 0
            }

            var opcode = (OscWebSocketOpcode)(b0 & 0x0F);
            var fin = (b0 & 0x80) != 0;
            var masked = (b1 & 0x80) != 0;
            if (!masked)
            {
                return OscWebSocketParseResult.ProtocolError;
            }

            long length = b1 & 0x7F;
            var position = 2;
            if (length == 126)
            {
                if (count < 4) return OscWebSocketParseResult.Incomplete;
                length = (buffer[offset + 2] << 8) | buffer[offset + 3];
                position = 4;
            }
            else if (length == 127)
            {
                if (count < 10) return OscWebSocketParseResult.Incomplete;
                length = 0;
                for (var i = 0; i < 8; i++)
                {
                    length = (length << 8) | buffer[offset + 2 + i];
                }

                if (length < 0) return OscWebSocketParseResult.ProtocolError;
                position = 10;
            }

            var isControl = ((int)opcode & 0x8) != 0;
            if (isControl && (length > 125 || !fin))
            {
                return OscWebSocketParseResult.ProtocolError;
            }

            if (length > maxPayload)
            {
                return OscWebSocketParseResult.TooLarge;
            }

            if (count < position + 4 + length)
            {
                return OscWebSocketParseResult.Incomplete;
            }

            var maskStart = offset + position;
            var payload = new byte[length];
            for (var i = 0; i < length; i++)
            {
                payload[i] = (byte)(buffer[maskStart + 4 + i] ^ buffer[maskStart + (i & 3)]);
            }

            frame = new OscWebSocketFrame(fin, opcode, payload);
            consumed = position + 4 + (int)length;
            return OscWebSocketParseResult.Ok;
        }

        /// <summary>サーバーから送るフレーム(マスクなし、FIN あり)。</summary>
        public static byte[] EncodeFrame(OscWebSocketOpcode opcode, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            var length = payload.Length;
            var headerLength = length < 126 ? 2 : length <= 0xFFFF ? 4 : 10;
            var frame = new byte[headerLength + length];
            frame[0] = (byte)(0x80 | ((int)opcode & 0x0F));
            if (length < 126)
            {
                frame[1] = (byte)length;
            }
            else if (length <= 0xFFFF)
            {
                frame[1] = 126;
                frame[2] = (byte)(length >> 8);
                frame[3] = (byte)length;
            }
            else
            {
                frame[1] = 127;
                for (var i = 0; i < 8; i++)
                {
                    frame[2 + i] = (byte)((long)length >> (56 - 8 * i));
                }
            }

            Buffer.BlockCopy(payload, 0, frame, headerLength, length);
            return frame;
        }

        public static byte[] EncodeText(string text)
        {
            return EncodeFrame(OscWebSocketOpcode.Text, OscHttp.Utf8.GetBytes(text ?? string.Empty));
        }

        /// <summary>閉じるフレーム(状態コード + 理由)。</summary>
        public static byte[] EncodeClose(int code)
        {
            return EncodeFrame(OscWebSocketOpcode.Close, new[] { (byte)(code >> 8), (byte)code });
        }
    }

    /// <summary>OSCQuery の WebSocket の命令(クライアント → サーバー)。</summary>
    public static class OscQueryCommand
    {
        public const string Listen = "LISTEN";
        public const string Ignore = "IGNORE";

        /// <summary><c>{"COMMAND":"LISTEN","DATA":"/a"}</c> を読む。形が違えば false。</summary>
        public static bool TryParse(string text, out string command, out string data)
        {
            command = null;
            data = null;
            if (!OscQueryJson.TryParseFlatObject(text, out var fields) || !fields.TryGetValue("COMMAND", out command))
            {
                command = null;
                return false;
            }

            fields.TryGetValue("DATA", out data);
            return true;
        }
    }
}
