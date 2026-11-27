using System.Collections.Generic;
using System.Net;
using System.Text;
using NUnit.Framework;

namespace OscDesk.Receiver.Tests
{
    // mDNS の応答側のパケット(HID-490)。ソケットを使わない部分
    public sealed class OscMdnsTests
    {
        private static OscMdnsService Service()
        {
            return new OscMdnsService("Stage (7090)", "oscdesk-pc-7090", 54321, new[] { IPAddress.Parse("192.168.1.20"), IPAddress.Parse("10.0.0.5") });
        }

        // 問い合わせを組み立てる(名前は圧縮しない。QU は class の最上位ビット)
        private static byte[] Query(ushort id, params (string name, int type, bool qu)[] questions)
        {
            var data = new List<byte> { (byte)(id >> 8), (byte)id, 0, 0, 0, (byte)questions.Length, 0, 0, 0, 0, 0, 0 };
            foreach (var question in questions)
            {
                foreach (var label in question.name.Split('.'))
                {
                    var bytes = Encoding.UTF8.GetBytes(label);
                    data.Add((byte)bytes.Length);
                    data.AddRange(bytes);
                }

                data.Add(0);
                data.Add((byte)(question.type >> 8));
                data.Add((byte)question.type);
                data.Add((byte)(question.qu ? 0x80 : 0));
                data.Add(1);
            }

            return data.ToArray();
        }

        private static int Count(byte[] packet, int index) => (packet[index] << 8) | packet[index + 1];

        [Test]
        public void Questions_are_parsed_including_compressed_names()
        {
            var query = Query(7, ("_oscjson._tcp.local", OscMdns.TypePtr, true));
            Assert.That(OscMdns.TryParse(query, query.Length, out var message), Is.True);
            Assert.That(message.Id, Is.EqualTo(7));
            Assert.That(message.IsResponse, Is.False);
            Assert.That(message.Questions.Count, Is.EqualTo(1));
            Assert.That(message.Questions[0].Name.ToString(), Is.EqualTo("_oscjson._tcp.local"));
            Assert.That(message.Questions[0].Type, Is.EqualTo(OscMdns.TypePtr));
            Assert.That(message.Questions[0].UnicastResponse, Is.True);

            // 2 問目の名前を 1 問目の "_tcp.local"(オフセット 21)への圧縮で書く
            var compressed = new List<byte>(Query(0, ("_oscjson._tcp.local", OscMdns.TypePtr, false)));
            compressed[5] = 2;
            compressed.AddRange(new byte[] { 3, (byte)'_', (byte)'x', (byte)'y', 0xC0, 21, 0, 12, 0, 1 });
            Assert.That(OscMdns.TryParse(compressed.ToArray(), compressed.Count, out message), Is.True);
            Assert.That(message.Questions[1].Name.ToString(), Is.EqualTo("_xy._tcp.local"));
        }

        [Test]
        public void Broken_packets_and_pointer_loops_are_rejected()
        {
            Assert.That(OscMdns.TryParse(new byte[5], 5, out _), Is.False);
            var loop = new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 1, 0, 1 };
            Assert.That(OscMdns.TryParse(loop, loop.Length, out _), Is.False);
            var truncated = Query(0, ("_oscjson._tcp.local", OscMdns.TypePtr, false));
            Assert.That(OscMdns.TryParse(truncated, truncated.Length - 3, out _), Is.False);
        }

        [Test]
        public void Ptr_query_is_answered_with_ptr_and_srv_txt_a_in_additionals()
        {
            var query = Query(0, ("_OSCJSON._tcp.local", OscMdns.TypePtr, false)); // 大文字小文字は区別しない
            OscMdns.TryParse(query, query.Length, out var message);
            var response = OscMdns.BuildResponse(Service(), message, false);
            Assert.That(response, Is.Not.Null);
            Assert.That(Count(response, 2), Is.EqualTo(0x8400));
            Assert.That(Count(response, 4), Is.EqualTo(0), "マルチキャストの応答は問い合わせを写さない");
            Assert.That(Count(response, 6), Is.EqualTo(1));
            Assert.That(Count(response, 10), Is.EqualTo(4)); // SRV・TXT・A × 2
            var text = Encoding.UTF8.GetString(response);
            StringAssert.Contains("Stage (7090)", text);
            StringAssert.Contains("oscdesk-pc-7090", text);
            StringAssert.Contains("txtvers=1", text);
        }

        [Test]
        public void Legacy_unicast_query_echoes_the_id_and_question_with_short_ttl()
        {
            var query = Query(0x1234, ("Stage (7090)._oscjson._tcp.local", OscMdns.TypeSrv, false));
            OscMdns.TryParse(query, query.Length, out var message);
            var response = OscMdns.BuildResponse(Service(), message, true);
            Assert.That(Count(response, 0), Is.EqualTo(0x1234));
            Assert.That(Count(response, 4), Is.EqualTo(1));
            Assert.That(Count(response, 6), Is.EqualTo(1));
            Assert.That(Count(response, 10), Is.EqualTo(2)); // A × 2
            // 問い合わせを写した後の答え(SRV)の TTL が 10 秒
            var answerStart = query.Length;
            var nameLength = query.Length - 12 - 4;
            var ttlAt = answerStart + nameLength + 4;
            Assert.That((response[ttlAt] << 24) | (response[ttlAt + 1] << 16) | (response[ttlAt + 2] << 8) | response[ttlAt + 3], Is.EqualTo(OscMdns.LegacyUnicastTtl));
        }

        [Test]
        public void Host_a_query_and_service_enumeration_are_answered_and_others_are_not()
        {
            var a = Query(0, ("oscdesk-pc-7090.local", OscMdns.TypeA, false));
            OscMdns.TryParse(a, a.Length, out var message);
            Assert.That(Count(OscMdns.BuildResponse(Service(), message, false), 6), Is.EqualTo(2));

            var services = Query(0, ("_services._dns-sd._udp.local", OscMdns.TypePtr, false));
            OscMdns.TryParse(services, services.Length, out message);
            Assert.That(Count(OscMdns.BuildResponse(Service(), message, false), 6), Is.EqualTo(1));

            var other = Query(0, ("_http._tcp.local", OscMdns.TypePtr, false));
            OscMdns.TryParse(other, other.Length, out message);
            Assert.That(OscMdns.BuildResponse(Service(), message, false), Is.Null);
        }

        [Test]
        public void Announcement_lists_all_records_and_goodbye_has_zero_ttl()
        {
            var announcement = OscMdns.BuildAnnouncement(Service());
            Assert.That(Count(announcement, 6), Is.EqualTo(5)); // PTR・SRV・TXT・A × 2
            var goodbye = OscMdns.BuildAnnouncement(Service(), 0);
            // 先頭の PTR の TTL(見出し 12 + 名前 21 + type 2 + class 2)
            var ttlAt = 12 + 21 + 4;
            Assert.That(goodbye[ttlAt] | goodbye[ttlAt + 1] | goodbye[ttlAt + 2] | goodbye[ttlAt + 3], Is.EqualTo(0));
            Assert.That(OscMdns.TryParse(announcement, announcement.Length, out var parsed), Is.True);
            Assert.That(parsed.IsResponse, Is.True);
        }

        [Test]
        public void Instance_and_host_names_carry_the_port_and_fit_a_dns_label()
        {
            Assert.That(OscMdnsService.MakeInstanceName("Stage", 7090), Is.EqualTo("Stage (7090)"));
            Assert.That(OscMdnsService.MakeInstanceName(" ", 7090), Is.EqualTo("Unity (7090)"));
            var longName = OscMdnsService.MakeInstanceName(new string('舞', 40), 7090);
            Assert.That(Encoding.UTF8.GetByteCount(longName), Is.LessThanOrEqualTo(OscMdnsService.MaxLabelBytes));
            StringAssert.EndsWith(" (7090)", longName);
            Assert.That(OscMdnsService.MakeHostLabel("DESKTOP_AB 12", 7090), Is.EqualTo("oscdesk-desktop-ab-12-7090"));
            Assert.That(OscMdnsService.MakeHostLabel("日本", 7091), Is.EqualTo("oscdesk-7091"));
        }
    }
}
