using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;

namespace OscDesk.Receiver.Tests
{
    // OSCQuery のサーバーをループバックで実際に叩く(HID-490)。mDNS は LAN へ出さないよう切る
    public sealed class OscQueryServerTests
    {
        private OscQueryServer server;

        [SetUp]
        public void SetUp()
        {
            server = new OscQueryServer(new OscQueryServerOptions
            {
                Name = "Test (7090)",
                OscPort = 7090,
                AdvertiseMdns = false,
                BootId = () => "boot-xyz",
            });
            server.Publish(OscQueryTree.Build(new[]
            {
                new OscRegistryEntry("/mix/level", new OscOperation("level", OscBindingKind.Float, _ => null).WithRange(0f, 1f), null),
            }));
        }

        [TearDown]
        public void TearDown()
        {
            server.Dispose();
        }

        private string Get(string target, out int status)
        {
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, server.HttpPort);
                client.ReceiveTimeout = 3000;
                var stream = client.GetStream();
                var request = Encoding.ASCII.GetBytes("GET " + target + " HTTP/1.1\r\nHost: localhost\r\n\r\n");
                stream.Write(request, 0, request.Length);
                var text = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
                status = int.Parse(text.Substring(9, 3));
                var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                return text.Substring(split + 4);
            }
        }

        [Test]
        public void Http_serves_host_info_namespace_attributes_and_errors()
        {
            Assert.That(server.HttpPort, Is.GreaterThan(0));
            var hostInfo = Get("/?HOST_INFO", out var status);
            Assert.That(status, Is.EqualTo(200));
            Assert.That(hostInfo, Is.EqualTo(OscQueryHostInfo.ToJson("Test (7090)", 7090, server.HttpPort, "boot-xyz")));

            Assert.That(Get("/mix/level?RANGE", out status), Is.EqualTo("{\"RANGE\":[{\"MIN\":0,\"MAX\":1}]}"));
            StringAssert.Contains("\"level\"", Get("/", out status));
            Get("/mix/level?VALUE", out status);
            Assert.That(status, Is.EqualTo(204));
            Get("/missing", out status);
            Assert.That(status, Is.EqualTo(404));
        }

        [Test]
        public void Websocket_receives_path_changes_and_answers_ping()
        {
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, server.HttpPort);
                client.ReceiveTimeout = 3000;
                var stream = client.GetStream();
                var handshake = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                    + "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");
                stream.Write(handshake, 0, handshake.Length);
                var head = ReadHead(stream);
                StringAssert.StartsWith("HTTP/1.1 101", head);
                StringAssert.Contains("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", head);

                // 登録の後から WebSocket の購読者に加わるので、少し待ってから差し替える
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (server.WebSocketClientCount == 0 && DateTime.UtcNow < deadline)
                {
                    System.Threading.Thread.Sleep(10);
                }

                // 命令は受け付けるだけ(切らない)
                var listen = Masked(0x1, Encoding.UTF8.GetBytes("{\"COMMAND\":\"LISTEN\",\"DATA\":\"/mix/level\"}"));
                stream.Write(listen, 0, listen.Length);

                var changes = server.Publish(OscQueryTree.Build(new[]
                {
                    new OscRegistryEntry("/mix/level", new OscOperation("level", OscBindingKind.Float, _ => null).WithRange(0f, 1f), null),
                    new OscRegistryEntry("/member/m01/active", new OscOperation("active", OscBindingKind.Bool, _ => null), null),
                }));
                Assert.That(changes.Count, Is.EqualTo(1));
                Assert.That(ReadText(stream), Is.EqualTo("{\"COMMAND\":\"PATH_ADDED\",\"DATA\":\"/member\"}"));

                var ping = Masked(0x9, new byte[] { 1, 2 });
                stream.Write(ping, 0, ping.Length);
                var pong = ReadFrame(stream, out var opcode);
                Assert.That(opcode, Is.EqualTo(0xA));
                Assert.That(pong, Is.EqualTo(new byte[] { 1, 2 }));
            }
        }

        [Test]
        public void Bad_request_gets_400()
        {
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, server.HttpPort);
                client.ReceiveTimeout = 3000;
                var stream = client.GetStream();
                var bytes = Encoding.ASCII.GetBytes("NONSENSE\r\n\r\n");
                stream.Write(bytes, 0, bytes.Length);
                StringAssert.StartsWith("HTTP/1.1 400", new StreamReader(stream, Encoding.ASCII).ReadToEnd());
            }
        }

        private static byte[] Masked(int opcode, byte[] payload)
        {
            var mask = new byte[] { 0x11, 0x22, 0x33, 0x44 };
            var frame = new byte[2 + 4 + payload.Length];
            frame[0] = (byte)(0x80 | opcode);
            frame[1] = (byte)(0x80 | payload.Length);
            Buffer.BlockCopy(mask, 0, frame, 2, 4);
            for (var i = 0; i < payload.Length; i++)
            {
                frame[6 + i] = (byte)(payload[i] ^ mask[i & 3]);
            }

            return frame;
        }

        private static string ReadHead(Stream stream)
        {
            var builder = new StringBuilder();
            while (!builder.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                var b = stream.ReadByte();
                if (b < 0) break;
                builder.Append((char)b);
            }

            return builder.ToString();
        }

        private static string ReadText(Stream stream)
        {
            var payload = ReadFrame(stream, out var opcode);
            Assert.That(opcode, Is.EqualTo(0x1));
            return Encoding.UTF8.GetString(payload);
        }

        private static byte[] ReadFrame(Stream stream, out int opcode)
        {
            var b0 = stream.ReadByte();
            var b1 = stream.ReadByte();
            opcode = b0 & 0x0F;
            Assert.That(b1 & 0x80, Is.EqualTo(0), "サーバーのフレームはマスクしない");
            var length = b1 & 0x7F;
            if (length == 126)
            {
                length = (stream.ReadByte() << 8) | stream.ReadByte();
            }

            var payload = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = stream.Read(payload, read, length - read);
                if (n <= 0) break;
                read += n;
            }

            return payload;
        }
    }
}
