// OscMdns.cs — mDNS(RFC 6762)/ DNS-SD(RFC 6763)の応答側のパケット(HID-490)。OSCQuery のサービス _oscjson._tcp を告知する。
// ソケットは触らない(OscQueryServer が持つ)。外部のライブラリを使わない(D-095)。UnityEngine に依存しない。
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OscDesk.Receiver
{
    /// <summary>DNS の名前(ラベルの並び)。比べるときは ASCII の大文字小文字を区別しない(RFC 6762 §16)。</summary>
    public sealed class OscDnsName
    {
        public IReadOnlyList<string> Labels { get; }

        public OscDnsName(params string[] labels)
        {
            Labels = labels ?? Array.Empty<string>();
        }

        public static OscDnsName Parse(string dotted)
        {
            return new OscDnsName((dotted ?? string.Empty).TrimEnd('.').Split('.'));
        }

        public bool Matches(OscDnsName other)
        {
            if (other == null || other.Labels.Count != Labels.Count)
            {
                return false;
            }

            for (var i = 0; i < Labels.Count; i++)
            {
                if (!string.Equals(Labels[i], other.Labels[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        public override string ToString()
        {
            return string.Join(".", Labels);
        }
    }

    /// <summary>問い合わせ 1 つ。</summary>
    public readonly struct OscDnsQuestion
    {
        public OscDnsName Name { get; }
        public int Type { get; }

        /// <summary>QU ビット(ユニキャストでの応答を求める)。</summary>
        public bool UnicastResponse { get; }

        public OscDnsQuestion(OscDnsName name, int type, bool unicastResponse)
        {
            Name = name;
            Type = type;
            UnicastResponse = unicastResponse;
        }
    }

    /// <summary>受けた DNS のメッセージのうち、応答側が見る部分(ID・応答か・問い合わせ)。</summary>
    public sealed class OscDnsMessage
    {
        public ushort Id { get; }
        public bool IsResponse { get; }
        public IReadOnlyList<OscDnsQuestion> Questions { get; }

        public OscDnsMessage(ushort id, bool isResponse, IReadOnlyList<OscDnsQuestion> questions)
        {
            Id = id;
            IsResponse = isResponse;
            Questions = questions ?? Array.Empty<OscDnsQuestion>();
        }
    }

    /// <summary>告知するサービス 1 つ(インスタンス名・ホスト名・ポート・IPv4 のアドレス)。</summary>
    public sealed class OscMdnsService
    {
        public static readonly OscDnsName ServiceType = new OscDnsName("_oscjson", "_tcp", "local");
        public static readonly OscDnsName ServiceEnumeration = new OscDnsName("_services", "_dns-sd", "_udp", "local");

        /// <summary>DNS のラベルの上限(UTF-8 のバイト数)。</summary>
        public const int MaxLabelBytes = 63;

        public string InstanceName { get; }
        public string HostLabel { get; }
        public int Port { get; }
        public IReadOnlyList<IPAddress> Addresses { get; }

        public OscDnsName InstanceFullName => new OscDnsName(InstanceName, "_oscjson", "_tcp", "local");
        public OscDnsName HostFullName => new OscDnsName(HostLabel, "local");

        public OscMdnsService(string instanceName, string hostLabel, int port, IReadOnlyList<IPAddress> addresses)
        {
            InstanceName = ClipLabel(string.IsNullOrEmpty(instanceName) ? "OscDesk" : instanceName);
            HostLabel = ClipLabel(string.IsNullOrEmpty(hostLabel) ? "oscdesk" : hostLabel);
            Port = port;
            var v4 = new List<IPAddress>();
            if (addresses != null)
            {
                foreach (var address in addresses)
                {
                    if (address != null && address.AddressFamily == AddressFamily.InterNetwork && !v4.Contains(address))
                    {
                        v4.Add(address);
                    }
                }
            }

            Addresses = v4;
        }

        /// <summary>
        /// サービスのインスタンス名。同じ PC に Unity が 2 つ動いても重ならないよう、製品名に OSC の受信ポートを添える
        /// (HID-484 §6 の 8)。DNS のラベルの上限で切る。
        /// </summary>
        public static string MakeInstanceName(string productName, int oscPort)
        {
            var suffix = " (" + oscPort + ")";
            var name = string.IsNullOrWhiteSpace(productName) ? "Unity" : productName.Trim();
            name = name.Replace("\0", string.Empty);
            while (name.Length > 0 && Encoding.UTF8.GetByteCount(name + suffix) > MaxLabelBytes)
            {
                name = name.Substring(0, char.IsLowSurrogate(name[name.Length - 1]) && name.Length >= 2 ? name.Length - 2 : name.Length - 1);
            }

            return name + suffix;
        }

        /// <summary>SRV の宛先のホスト名(.local の前)。英小文字・数字・- だけにし、OS のホスト名と重ならないよう oscdesk- と ポートを添える。</summary>
        public static string MakeHostLabel(string machineName, int oscPort)
        {
            var clean = new StringBuilder();
            foreach (var c in (machineName ?? string.Empty).ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    clean.Append(c);
                }
                else if (clean.Length > 0 && clean[clean.Length - 1] != '-')
                {
                    clean.Append('-');
                }
            }

            var machine = clean.ToString().Trim('-');
            var label = "oscdesk-" + (machine.Length > 0 ? machine + "-" : string.Empty) + oscPort;
            return label.Length > MaxLabelBytes ? label.Substring(label.Length - MaxLabelBytes).TrimStart('-') : label;
        }

        private static string ClipLabel(string label)
        {
            while (label.Length > 0 && Encoding.UTF8.GetByteCount(label) > MaxLabelBytes)
            {
                label = label.Substring(0, char.IsLowSurrogate(label[label.Length - 1]) && label.Length >= 2 ? label.Length - 2 : label.Length - 1);
            }

            return label;
        }
    }

    /// <summary>mDNS のパケットの読み取りと、応答・告知の組み立て。</summary>
    public static class OscMdns
    {
        public const int Port = 5353;
        public static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");

        public const int TypeA = 1;
        public const int TypePtr = 12;
        public const int TypeTxt = 16;
        public const int TypeSrv = 33;
        public const int TypeAny = 255;

        /// <summary>TTL の既定(秒)。ホスト名と SRV は 120 秒(RFC 6762 §10)、ほかも同じにする。</summary>
        public const int DefaultTtl = 120;

        /// <summary>レガシーのユニキャストの問い合わせ(送り元のポートが 5353 でない)に返す TTL の上限(RFC 6762 §6.7)。</summary>
        public const int LegacyUnicastTtl = 10;

        private const int MaxQuestions = 32;
        private const int MaxPointerJumps = 16;
        private const int ClassIn = 1;
        private const int CacheFlush = 0x8000;

        /// <summary>DNS のメッセージの見出しと問い合わせを読む。読めなければ false。</summary>
        public static bool TryParse(byte[] data, int length, out OscDnsMessage message)
        {
            message = null;
            if (data == null || length < 12 || length > data.Length)
            {
                return false;
            }

            var id = (ushort)((data[0] << 8) | data[1]);
            var flags = (data[2] << 8) | data[3];
            var questionCount = (data[4] << 8) | data[5];
            if (questionCount > MaxQuestions)
            {
                return false;
            }

            var position = 12;
            var questions = new List<OscDnsQuestion>(questionCount);
            for (var i = 0; i < questionCount; i++)
            {
                if (!TryReadName(data, length, ref position, out var name) || position + 4 > length)
                {
                    return false;
                }

                var type = (data[position] << 8) | data[position + 1];
                var klass = (data[position + 2] << 8) | data[position + 3];
                position += 4;
                questions.Add(new OscDnsQuestion(name, type, (klass & CacheFlush) != 0));
            }

            message = new OscDnsMessage(id, (flags & 0x8000) != 0, questions);
            return true;
        }

        private static bool TryReadName(byte[] data, int length, ref int position, out OscDnsName name)
        {
            name = null;
            var labels = new List<string>();
            var cursor = position;
            var jumped = false;
            var jumps = 0;
            var total = 0;
            while (true)
            {
                if (cursor >= length)
                {
                    return false;
                }

                var size = data[cursor];
                if (size == 0)
                {
                    cursor += 1;
                    break;
                }

                if ((size & 0xC0) == 0xC0)
                {
                    if (cursor + 1 >= length || ++jumps > MaxPointerJumps)
                    {
                        return false;
                    }

                    var target = ((size & 0x3F) << 8) | data[cursor + 1];
                    if (!jumped)
                    {
                        position = cursor + 2;
                        jumped = true;
                    }

                    if (target >= length)
                    {
                        return false;
                    }

                    cursor = target;
                    continue;
                }

                if ((size & 0xC0) != 0 || cursor + 1 + size > length)
                {
                    return false;
                }

                total += size + 1;
                if (total > 255)
                {
                    return false;
                }

                labels.Add(Encoding.UTF8.GetString(data, cursor + 1, size));
                cursor += 1 + size;
            }

            if (!jumped)
            {
                position = cursor;
            }

            name = new OscDnsName(labels.ToArray());
            return true;
        }

        /// <summary>
        /// 問い合わせへの応答。答えるものが無ければ null。legacyUnicast(送り元のポートが 5353 でない)なら、ID と問い合わせを写し、
        /// キャッシュの消去のビットを立てず、TTL を 10 秒までにする(RFC 6762 §6.7)。
        /// </summary>
        public static byte[] BuildResponse(OscMdnsService service, OscDnsMessage query, bool legacyUnicast)
        {
            if (service == null || query == null || query.IsResponse)
            {
                return null;
            }

            var answers = new List<Record>();
            var additionals = new List<Record>();
            var ttl = legacyUnicast ? LegacyUnicastTtl : DefaultTtl;
            var flush = !legacyUnicast;
            foreach (var question in query.Questions)
            {
                var any = question.Type == TypeAny;
                if (question.Name.Matches(OscMdnsService.ServiceType) && (any || question.Type == TypePtr))
                {
                    Add(answers, PtrRecord(service, ttl));
                    Add(additionals, SrvRecord(service, ttl, flush));
                    Add(additionals, TxtRecord(service, ttl, flush));
                    foreach (var a in ARecords(service, ttl, flush)) Add(additionals, a);
                }
                else if (question.Name.Matches(OscMdnsService.ServiceEnumeration) && (any || question.Type == TypePtr))
                {
                    Add(answers, EnumerationRecord(ttl));
                }
                else if (question.Name.Matches(service.InstanceFullName))
                {
                    if (any || question.Type == TypeSrv)
                    {
                        Add(answers, SrvRecord(service, ttl, flush));
                        foreach (var a in ARecords(service, ttl, flush)) Add(additionals, a);
                    }

                    if (any || question.Type == TypeTxt)
                    {
                        Add(answers, TxtRecord(service, ttl, flush));
                    }
                }
                else if (question.Name.Matches(service.HostFullName) && (any || question.Type == TypeA))
                {
                    foreach (var a in ARecords(service, ttl, flush)) Add(answers, a);
                }
            }

            if (answers.Count == 0)
            {
                return null;
            }

            // 答えに入れたものは追加の欄に重ねない
            additionals.RemoveAll(record => answers.Exists(answer => answer.SameAs(record)));
            return Encode(legacyUnicast ? query.Id : (ushort)0, legacyUnicast ? query.Questions : null, answers, additionals);
        }

        /// <summary>告知(求められていない応答)。PTR・SRV・TXT・A を答えに並べる。ttl 0 で取り下げ(goodbye)。</summary>
        public static byte[] BuildAnnouncement(OscMdnsService service, int ttl = DefaultTtl)
        {
            var answers = new List<Record> { PtrRecord(service, ttl), SrvRecord(service, ttl, true), TxtRecord(service, ttl, true) };
            answers.AddRange(ARecords(service, ttl, true));
            return Encode(0, null, answers, new List<Record>());
        }

        private static void Add(List<Record> records, Record record)
        {
            if (!records.Exists(existing => existing.SameAs(record)))
            {
                records.Add(record);
            }
        }

        private sealed class Record
        {
            public OscDnsName Name;
            public int Type;
            public int Class;
            public int Ttl;
            public Action<List<byte>> WriteData;
            public string Key;

            public bool SameAs(Record other)
            {
                return other.Type == Type && other.Name.Matches(Name) && other.Key == Key;
            }
        }

        private static Record PtrRecord(OscMdnsService service, int ttl)
        {
            var target = service.InstanceFullName;
            return new Record { Name = OscMdnsService.ServiceType, Type = TypePtr, Class = ClassIn, Ttl = ttl, WriteData = data => WriteName(data, target), Key = string.Empty };
        }

        private static Record EnumerationRecord(int ttl)
        {
            return new Record
            {
                Name = OscMdnsService.ServiceEnumeration, Type = TypePtr, Class = ClassIn, Ttl = ttl,
                WriteData = data => WriteName(data, OscMdnsService.ServiceType), Key = string.Empty
            };
        }

        private static Record SrvRecord(OscMdnsService service, int ttl, bool flush)
        {
            return new Record
            {
                Name = service.InstanceFullName, Type = TypeSrv, Class = ClassIn | (flush ? CacheFlush : 0), Ttl = ttl, Key = string.Empty,
                WriteData = data =>
                {
                    WriteUInt16(data, 0); // priority
                    WriteUInt16(data, 0); // weight
                    WriteUInt16(data, service.Port);
                    WriteName(data, service.HostFullName);
                }
            };
        }

        // TXT は txtvers=1 だけ(中身には頼らない。HID-484 §6 の 6)
        private static Record TxtRecord(OscMdnsService service, int ttl, bool flush)
        {
            return new Record
            {
                Name = service.InstanceFullName, Type = TypeTxt, Class = ClassIn | (flush ? CacheFlush : 0), Ttl = ttl, Key = string.Empty,
                WriteData = data =>
                {
                    var entry = Encoding.ASCII.GetBytes("txtvers=1");
                    data.Add((byte)entry.Length);
                    data.AddRange(entry);
                }
            };
        }

        private static List<Record> ARecords(OscMdnsService service, int ttl, bool flush)
        {
            var records = new List<Record>();
            foreach (var address in service.Addresses)
            {
                var bytes = address.GetAddressBytes();
                records.Add(new Record
                {
                    Name = service.HostFullName, Type = TypeA, Class = ClassIn | (flush ? CacheFlush : 0), Ttl = ttl, Key = address.ToString(),
                    WriteData = data => data.AddRange(bytes)
                });
            }

            return records;
        }

        private static byte[] Encode(ushort id, IReadOnlyList<OscDnsQuestion> questions, List<Record> answers, List<Record> additionals)
        {
            var data = new List<byte>(512);
            WriteUInt16(data, id);
            WriteUInt16(data, 0x8400); // 応答 + 権威あり
            WriteUInt16(data, questions?.Count ?? 0);
            WriteUInt16(data, answers.Count);
            WriteUInt16(data, 0);
            WriteUInt16(data, additionals.Count);
            if (questions != null)
            {
                foreach (var question in questions)
                {
                    WriteName(data, question.Name);
                    WriteUInt16(data, question.Type);
                    WriteUInt16(data, ClassIn);
                }
            }

            foreach (var record in answers) WriteRecord(data, record);
            foreach (var record in additionals) WriteRecord(data, record);
            return data.ToArray();
        }

        private static void WriteRecord(List<byte> data, Record record)
        {
            WriteName(data, record.Name);
            WriteUInt16(data, record.Type);
            WriteUInt16(data, record.Class);
            WriteUInt16(data, (record.Ttl >> 16) & 0xFFFF);
            WriteUInt16(data, record.Ttl & 0xFFFF);
            var lengthAt = data.Count;
            WriteUInt16(data, 0);
            record.WriteData(data);
            var length = data.Count - lengthAt - 2;
            data[lengthAt] = (byte)(length >> 8);
            data[lengthAt + 1] = (byte)length;
        }

        // 名前は圧縮せずに書く(パケットは小さく、圧縮の誤りで読めなくなる方が困る)
        private static void WriteName(List<byte> data, OscDnsName name)
        {
            foreach (var label in name.Labels)
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                var length = Math.Min(bytes.Length, OscMdnsService.MaxLabelBytes);
                if (length == 0)
                {
                    continue;
                }

                data.Add((byte)length);
                for (var i = 0; i < length; i++)
                {
                    data.Add(bytes[i]);
                }
            }

            data.Add(0);
        }

        private static void WriteUInt16(List<byte> data, int value)
        {
            data.Add((byte)(value >> 8));
            data.Add((byte)value);
        }
    }
}
