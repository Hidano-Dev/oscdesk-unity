using System;
using System.Text;
using NUnit.Framework;

namespace OscDesk.Receiver.Tests
{
    // OSCQuery の HTTP の要求・応答と WebSocket のフレーム(HID-490)。ソケットを使わない部分
    public sealed class OscQueryHttpTests
    {
        private static OscHttpRequest Parse(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            Assert.That(OscHttp.TryParse(bytes, bytes.Length, out var request, out var consumed), Is.EqualTo(OscHttpParseResult.Ok));
            Assert.That(consumed, Is.EqualTo(bytes.Length));
            return request;
        }

        private static OscQueryTree Tree()
        {
            return OscQueryTree.Build(new[]
            {
                new OscRegistryEntry("/mix/level", new OscOperation("level", OscBindingKind.Float, _ => null), null),
                new OscRegistryEntry("/cue/go", new OscOperation("go", OscBindingKind.Trigger, _ => null), null),
            });
        }

        [Test]
        public void Request_line_headers_path_and_query_are_read()
        {
            var request = Parse("GET /mix/level?TYPE HTTP/1.1\r\nHost: x\r\nAccept: a\r\nAccept: b\r\n\r\n");
            Assert.That(request.Method, Is.EqualTo("GET"));
            Assert.That(request.Path, Is.EqualTo("/mix/level"));
            Assert.That(request.Query, Is.EqualTo("TYPE"));
            Assert.That(request.Header("host"), Is.EqualTo("x"));
            Assert.That(request.Header("Accept"), Is.EqualTo("a, b"));
        }

        [Test]
        public void Percent_encoded_paths_are_decoded_and_bad_ones_rejected()
        {
            Assert.That(Parse("GET /a%2Db?HOST_INFO HTTP/1.1\r\n\r\n").Path, Is.EqualTo("/a-b"));
            var bad = Encoding.ASCII.GetBytes("GET /a%zz HTTP/1.1\r\n\r\n");
            Assert.That(OscHttp.TryParse(bad, bad.Length, out _, out _), Is.EqualTo(OscHttpParseResult.Bad));
            var notHttp = Encoding.ASCII.GetBytes("HELLO\r\n\r\n");
            Assert.That(OscHttp.TryParse(notHttp, notHttp.Length, out _, out _), Is.EqualTo(OscHttpParseResult.Bad));
        }

        [Test]
        public void Partial_and_oversized_headers_are_told_apart()
        {
            var partial = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n");
            Assert.That(OscHttp.TryParse(partial, partial.Length, out _, out _), Is.EqualTo(OscHttpParseResult.Incomplete));
            var huge = new byte[OscHttp.MaxHeaderBytes];
            for (var i = 0; i < huge.Length; i++) huge[i] = (byte)'a';
            Assert.That(OscHttp.TryParse(huge, huge.Length, out _, out _), Is.EqualTo(OscHttpParseResult.TooLarge));
        }

        [Test]
        public void Respond_answers_host_info_nodes_attributes_204_404_and_405()
        {
            var tree = Tree();
            var hostInfo = OscQueryHostInfo.ToJson("n", 1, 2, "b");
            Assert.That(Body(OscHttp.Respond(Parse("GET /?HOST_INFO HTTP/1.1\r\n\r\n"), tree, hostInfo)), Is.EqualTo(hostInfo));
            Assert.That(Body(OscHttp.Respond(Parse("GET /mix/level HTTP/1.1\r\n\r\n"), tree, hostInfo)),
                Is.EqualTo("{\"FULL_PATH\":\"/mix/level\",\"TYPE\":\"f\",\"ACCESS\":3}"));
            Assert.That(Body(OscHttp.Respond(Parse("GET /mix/level?TYPE HTTP/1.1\r\n\r\n"), tree, hostInfo)), Is.EqualTo("{\"TYPE\":\"f\"}"));
            Assert.That(OscHttp.Respond(Parse("GET /mix/level?VALUE HTTP/1.1\r\n\r\n"), tree, hostInfo).Status, Is.EqualTo(204));
            Assert.That(OscHttp.Respond(Parse("GET /mix/level?CLIPMODE HTTP/1.1\r\n\r\n"), tree, hostInfo).Status, Is.EqualTo(204));
            Assert.That(OscHttp.Respond(Parse("GET /nothing HTTP/1.1\r\n\r\n"), tree, hostInfo).Status, Is.EqualTo(404));
            Assert.That(OscHttp.Respond(Parse("POST / HTTP/1.1\r\n\r\n"), tree, hostInfo).Status, Is.EqualTo(405));
        }

        [Test]
        public void Response_bytes_have_status_length_and_close()
        {
            var text = Encoding.UTF8.GetString(OscHttpResponse.Json("{\"a\":\"あ\"}").ToBytes());
            StringAssert.StartsWith("HTTP/1.1 200 OK\r\n", text);
            StringAssert.Contains("Content-Type: application/json; charset=utf-8\r\n", text);
            StringAssert.Contains("Content-Length: 11\r\n", text); // "あ" は 3 バイト
            StringAssert.Contains("Connection: close\r\n", text);
            StringAssert.EndsWith("\r\n\r\n{\"a\":\"あ\"}", text);

            var noContent = Encoding.ASCII.GetString(new OscHttpResponse(204, "No Content").ToBytes());
            Assert.That(noContent, Does.Not.Contain("Content-Length"));
            var head = Encoding.UTF8.GetString(OscHttpResponse.Json("{}").ToBytes(false));
            StringAssert.EndsWith("\r\n\r\n", head);
        }

        [Test]
        public void Websocket_accept_matches_rfc6455_example()
        {
            Assert.That(OscWebSocket.ComputeAccept("dGhlIHNhbXBsZSBub25jZQ=="), Is.EqualTo("s3pPLMBiTxaQ9kYGzzhZRbK+xOo="));
            var request = Parse("GET / HTTP/1.1\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");
            Assert.That(OscWebSocket.IsUpgrade(request), Is.True);
            var handshake = Encoding.ASCII.GetString(OscWebSocket.BuildHandshake(request, out var accepted));
            Assert.That(accepted, Is.True);
            StringAssert.StartsWith("HTTP/1.1 101 Switching Protocols\r\n", handshake);
            StringAssert.Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n", handshake);
            Assert.That(OscWebSocket.IsUpgrade(Parse("GET / HTTP/1.1\r\n\r\n")), Is.False);
        }

        [Test]
        public void Masked_client_frames_are_read_and_unmasked_ones_rejected()
        {
            // RFC 6455 §5.7 の "Hello"(マスクあり)
            var frame = new byte[] { 0x81, 0x85, 0x37, 0xfa, 0x21, 0x3d, 0x7f, 0x9f, 0x4d, 0x51, 0x58 };
            Assert.That(OscWebSocket.TryReadFrame(frame, 0, frame.Length, out var read, out var used), Is.EqualTo(OscWebSocketParseResult.Ok));
            Assert.That(used, Is.EqualTo(frame.Length));
            Assert.That(read.Fin, Is.True);
            Assert.That(read.Opcode, Is.EqualTo(OscWebSocketOpcode.Text));
            Assert.That(Encoding.UTF8.GetString(read.Payload), Is.EqualTo("Hello"));

            Assert.That(OscWebSocket.TryReadFrame(frame, 0, 5, out _, out _), Is.EqualTo(OscWebSocketParseResult.Incomplete));
            var unmasked = new byte[] { 0x81, 0x05, 0x48, 0x65, 0x6c, 0x6c, 0x6f };
            Assert.That(OscWebSocket.TryReadFrame(unmasked, 0, unmasked.Length, out _, out _), Is.EqualTo(OscWebSocketParseResult.ProtocolError));
            var huge = new byte[] { 0x82, 0xFF, 0, 0, 0, 0, 0, 0x10, 0, 0, 1, 2, 3, 4 };
            Assert.That(OscWebSocket.TryReadFrame(huge, 0, huge.Length, out _, out _), Is.EqualTo(OscWebSocketParseResult.TooLarge));
        }

        [Test]
        public void Server_frames_use_the_short_and_extended_lengths()
        {
            Assert.That(OscWebSocket.EncodeText("Hello"), Is.EqualTo(new byte[] { 0x81, 0x05, 0x48, 0x65, 0x6c, 0x6c, 0x6f }));
            var medium = OscWebSocket.EncodeFrame(OscWebSocketOpcode.Binary, new byte[300]);
            Assert.That(medium[1], Is.EqualTo(126));
            Assert.That((medium[2] << 8) | medium[3], Is.EqualTo(300));
            Assert.That(medium.Length, Is.EqualTo(304));
            var large = OscWebSocket.EncodeFrame(OscWebSocketOpcode.Binary, new byte[70000]);
            Assert.That(large[1], Is.EqualTo(127));
            Assert.That(large.Length, Is.EqualTo(70010));
        }

        [Test]
        public void Listen_command_is_parsed_from_flat_json()
        {
            Assert.That(OscQueryCommand.TryParse("{\"COMMAND\":\"LISTEN\",\"DATA\":\"/mix/level\"}", out var command, out var data), Is.True);
            Assert.That(command, Is.EqualTo(OscQueryCommand.Listen));
            Assert.That(data, Is.EqualTo("/mix/level"));
            Assert.That(OscQueryCommand.TryParse(" { \"COMMAND\" : \"IGNORE\" , \"DATA\" : \"/a\\u0062\" } ", out command, out data), Is.True);
            Assert.That(data, Is.EqualTo("/ab"));
            Assert.That(OscQueryCommand.TryParse("{\"COMMAND\":1}", out _, out _), Is.False);
            Assert.That(OscQueryCommand.TryParse("not json", out _, out _), Is.False);
        }

        [Test]
        public void Json_strings_escape_quotes_controls_and_line_separators()
        {
            var builder = new StringBuilder();
            OscQueryJson.WriteString(builder, "a\"b\\c\n\u0001" + (char)0x2028 + "日本");
            Assert.That(builder.ToString(), Is.EqualTo("\"a\\\"b\\\\c\\n\\u0001\\u2028日本\""));
        }

        private static string Body(OscHttpResponse response)
        {
            Assert.That(response.Status, Is.EqualTo(200));
            return Encoding.UTF8.GetString(response.Body);
        }
    }
}
