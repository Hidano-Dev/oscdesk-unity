// OscQueryNamespace.cs — Registry の登録から OSCQuery の名前空間の木を作り、JSON にする(DESIGN.md D-164・HID-490)。
// UnityEngine に依存しない。木は作り終えたら書き換えないので、HTTP のスレッドからそのまま読める。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OscDesk.Receiver
{
    /// <summary>引数 1 つの RANGE(MIN / MAX / VALS)。どれも無ければ空の制約({})。</summary>
    public sealed class OscQueryRange
    {
        /// <summary>下限(int か float)。無ければ null。</summary>
        public object Min { get; }

        /// <summary>上限(int か float)。無ければ null。</summary>
        public object Max { get; }

        /// <summary>選べる値(int / float / string)。無ければ null。</summary>
        public IReadOnlyList<object> Vals { get; }

        public bool IsEmpty => Min == null && Max == null && Vals == null;

        public OscQueryRange(object min, object max, IReadOnlyList<object> vals)
        {
            Min = min;
            Max = max;
            Vals = vals != null && vals.Count > 0 ? vals : null;
        }

        internal void Write(StringBuilder builder)
        {
            builder.Append('{');
            var first = true;
            if (Min != null)
            {
                builder.Append("\"MIN\":");
                OscQueryJson.WriteValue(builder, Min);
                first = false;
            }

            if (Max != null)
            {
                builder.Append(first ? string.Empty : ",").Append("\"MAX\":");
                OscQueryJson.WriteValue(builder, Max);
                first = false;
            }

            if (Vals != null)
            {
                builder.Append(first ? string.Empty : ",").Append("\"VALS\":[");
                for (var i = 0; i < Vals.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    OscQueryJson.WriteValue(builder, Vals[i]);
                }

                builder.Append(']');
            }

            builder.Append('}');
        }
    }

    /// <summary>名前空間のノード 1 つ。アドレスの 1 段に当たる。値を受けるノード(葉)は TYPE か ACCESS を持つ。</summary>
    public sealed class OscQueryNode
    {
        private readonly SortedDictionary<string, OscQueryNode> children = new SortedDictionary<string, OscQueryNode>(StringComparer.Ordinal);

        internal OscQueryNode(string fullPath, string name)
        {
            FullPath = fullPath;
            Name = name;
        }

        public string FullPath { get; }

        /// <summary>この段の名前(根は空)。</summary>
        public string Name { get; }

        /// <summary>OSC の型タグの並び(例 "f"・"ifs")。値を取らない操作と、段だけのノードは null。</summary>
        public string Type { get; internal set; }

        /// <summary>0 = 値なし(段だけ)、2 = 送るだけ(きっかけ・命令)、3 = 値を持つ(保持して送り直してよい)。</summary>
        public int Access { get; internal set; }

        /// <summary>人が読む名前(UTF-8)。無ければ空。</summary>
        public string Description { get; internal set; } = string.Empty;

        /// <summary>引数ごとの RANGE。無ければ null。</summary>
        public IReadOnlyList<OscQueryRange> Range { get; internal set; }

        /// <summary>登録から作った、値を受けるノードか(段だけのノードでない)。</summary>
        public bool IsMethod { get; internal set; }

        public IEnumerable<OscQueryNode> Contents => children.Values;

        public int ChildCount => children.Count;

        internal SortedDictionary<string, OscQueryNode> Children => children;

        public bool TryGetChild(string name, out OscQueryNode child)
        {
            return children.TryGetValue(name, out child);
        }

        /// <summary>差分(PATH_CHANGED)を決めるための、子を除いた属性の要約。</summary>
        internal string Signature()
        {
            var builder = new StringBuilder();
            builder.Append(Type ?? "-").Append('\u0001').Append(Access.ToString(CultureInfo.InvariantCulture)).Append('\u0001')
                .Append(Description).Append('\u0001');
            if (Range != null)
            {
                OscQueryTree.WriteRange(builder, Range);
            }

            return builder.ToString();
        }
    }

    /// <summary>名前空間に載せなかった登録(警告に使う)。</summary>
    public readonly struct OscQuerySkipped
    {
        public string Address { get; }
        public string Reason { get; }

        public OscQuerySkipped(string address, string reason)
        {
            Address = address;
            Reason = reason;
        }
    }

    public enum OscQueryPathChangeKind
    {
        Added,
        Removed,
        Changed
    }

    /// <summary>名前空間の変化 1 件。WebSocket の PATH_ADDED / PATH_REMOVED / PATH_CHANGED で知らせる。</summary>
    public readonly struct OscQueryPathChange
    {
        public OscQueryPathChangeKind Kind { get; }
        public string Path { get; }

        public OscQueryPathChange(OscQueryPathChangeKind kind, string path)
        {
            Kind = kind;
            Path = path;
        }

        public string Command
        {
            get
            {
                switch (Kind)
                {
                    case OscQueryPathChangeKind.Added: return "PATH_ADDED";
                    case OscQueryPathChangeKind.Removed: return "PATH_REMOVED";
                    default: return "PATH_CHANGED";
                }
            }
        }

        /// <summary>WebSocket のテキストのフレームで送る JSON(<c>{"COMMAND":"PATH_ADDED","DATA":"/a"}</c>)。</summary>
        public string ToJson()
        {
            var builder = new StringBuilder();
            builder.Append("{\"COMMAND\":");
            OscQueryJson.WriteString(builder, Command);
            builder.Append(",\"DATA\":");
            OscQueryJson.WriteString(builder, Path);
            builder.Append('}');
            return builder.ToString();
        }

        public override string ToString()
        {
            return Command + " " + Path;
        }
    }

    /// <summary>
    /// OSCQuery の名前空間の木(作り終えたら変えない)。<see cref="Build"/> で Registry の登録の並びから作る。
    /// 載せる規則(HID-484 §5・§6):
    ///   - パスは ASCII の印字できる文字だけ(空白と OSC の予約文字 <c># * , / ? [ ] { }</c> を除く)。満たさない登録は載せない
    ///   - 型: Int → i、Float → f、String → s、Bool → T(他の道具との互換。受け口は T/F と i の 0/1 の両方を受ける)、
    ///     Trigger → TYPE なし、組み合わせの型(基本型だけの Record)→ フィールドの型タグを並べたもの。
    ///     Record(型の無いもの)・Strings は OSCQuery で表せないので載せない
    ///   - ACCESS: 値を持つ操作は 3、きっかけ(Trigger・AsCommand)は 2。VALUE は持たないので書かない(値の無い VALUE は省く)
    ///   - RANGE: 選択肢は VALS、範囲は MIN / MAX。DESCRIPTION: 操作の Label、段は登録の TargetDescription
    /// </summary>
    public sealed class OscQueryTree
    {
        /// <summary>ノードの数の上限(段を含む)。超える登録は載せない(受け側の木の大きさの上限の目安。HID-484 §6 の 10)。</summary>
        public const int MaxNodes = 8192;

        /// <summary>パスの段の数の上限。</summary>
        public const int MaxDepth = 32;

        /// <summary>DESCRIPTION の文字数の上限。</summary>
        public const int MaxDescriptionChars = 1024;

        public const string ReasonInvalidPath = "path is not plain ASCII without OSC pattern characters";
        public const string ReasonUnsupportedType = "type cannot be described in OSCQuery (record without a value type, strings)";
        public const string ReasonTooLarge = "namespace is full";

        private readonly Dictionary<string, OscQueryNode> byPath = new Dictionary<string, OscQueryNode>(StringComparer.Ordinal);
        private readonly List<OscQuerySkipped> skipped = new List<OscQuerySkipped>();

        private OscQueryTree()
        {
            Root = new OscQueryNode("/", string.Empty);
            byPath.Add("/", Root);
        }

        public OscQueryNode Root { get; }

        public int NodeCount => byPath.Count;

        /// <summary>載せなかった登録と理由(登録の順)。</summary>
        public IReadOnlyList<OscQuerySkipped> Skipped => skipped;

        /// <summary>登録の無い空の木。</summary>
        public static OscQueryTree Empty() => new OscQueryTree();

        public bool TryFind(string path, out OscQueryNode node)
        {
            if (path == null)
            {
                node = null;
                return false;
            }

            if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
            {
                path = path.TrimEnd('/');
                if (path.Length == 0)
                {
                    path = "/";
                }
            }

            return byPath.TryGetValue(path, out node);
        }

        /// <summary>すべてのパス(根を含む)。</summary>
        public IEnumerable<string> Paths => byPath.Keys;

        /// <summary>登録の並びから木を作る。</summary>
        public static OscQueryTree Build(IEnumerable<OscRegistryEntry> entries)
        {
            var tree = new OscQueryTree();
            var descriptions = new List<KeyValuePair<string, string>>();
            if (entries == null)
            {
                return tree;
            }

            foreach (var entry in entries)
            {
                if (entry == null)
                {
                    continue;
                }

                if (!TrySplitPath(entry.Address, out var segments))
                {
                    tree.skipped.Add(new OscQuerySkipped(entry.Address, ReasonInvalidPath));
                    continue;
                }

                if (!TryDescribe(entry.Operation, out var type, out var access, out var range))
                {
                    tree.skipped.Add(new OscQuerySkipped(entry.Address, ReasonUnsupportedType));
                    continue;
                }

                if (!tree.TryInsert(segments, out var node))
                {
                    tree.skipped.Add(new OscQuerySkipped(entry.Address, ReasonTooLarge));
                    continue;
                }

                node.IsMethod = true;
                node.Type = type;
                node.Access = access;
                node.Range = range;
                node.Description = CleanDescription(entry.Operation.Label);
                if (!string.IsNullOrEmpty(entry.TargetDescription) && segments.Length >= 2)
                {
                    descriptions.Add(new KeyValuePair<string, string>(ParentPath(entry.Address), entry.TargetDescription));
                }
            }

            // 段の表示名は、その段の操作の Label が無いときだけ。同じ段に複数の登録が名前を持てば、先の登録が勝つ
            foreach (var pair in descriptions)
            {
                if (tree.byPath.TryGetValue(pair.Key, out var parent) && parent.Description.Length == 0)
                {
                    parent.Description = CleanDescription(pair.Value);
                }
            }

            return tree;
        }

        private bool TryInsert(string[] segments, out OscQueryNode node)
        {
            node = null;
            var missing = 0;
            var current = Root;
            var depth = 0;
            for (; depth < segments.Length; depth++)
            {
                if (!current.TryGetChild(segments[depth], out var child))
                {
                    break;
                }

                current = child;
            }

            missing = segments.Length - depth;
            if (byPath.Count + missing > MaxNodes)
            {
                return false;
            }

            var path = current.FullPath == "/" ? string.Empty : current.FullPath;
            for (; depth < segments.Length; depth++)
            {
                path += "/" + segments[depth];
                var child = new OscQueryNode(path, segments[depth]);
                current.Children.Add(segments[depth], child);
                byPath.Add(path, child);
                current = child;
            }

            node = current;
            return true;
        }

        private static string ParentPath(string address)
        {
            var index = address.LastIndexOf('/');
            return index <= 0 ? "/" : address.Substring(0, index);
        }

        private static string CleanDescription(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(Math.Min(text.Length, MaxDescriptionChars));
            for (var i = 0; i < text.Length && builder.Length < MaxDescriptionChars; i++)
            {
                var c = text[i];
                if (c == '\0')
                {
                    continue;
                }

                // UTF-8 にできない単独のサロゲートは落とす(HTTP の本文を UTF-8 で書くため)
                if (char.IsHighSurrogate(c))
                {
                    var paired = i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
                    if (paired)
                    {
                        if (builder.Length + 2 <= MaxDescriptionChars)
                        {
                            builder.Append(c).Append(text[i + 1]);
                        }

                        i += 1;
                    }

                    continue;
                }

                if (char.IsLowSurrogate(c))
                {
                    continue;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        /// <summary>
        /// OSCQuery のパスに使えるアドレスなら段に分ける。ASCII の印字できる文字(0x21〜0x7E)だけで、空の段と
        /// OSC の予約文字(<c># * , ? [ ] { }</c>)を含まない(パターンと区別できなくなるため)。/sys と /oscdesk は載せない。
        /// </summary>
        public static bool TrySplitPath(string address, out string[] segments)
        {
            segments = null;
            if (string.IsNullOrEmpty(address) || address[0] != '/' || address.Length == 1)
            {
                return false;
            }

            var parts = address.Substring(1).Split('/');
            if (parts.Length > MaxDepth)
            {
                return false;
            }

            foreach (var part in parts)
            {
                if (part.Length == 0)
                {
                    return false;
                }

                foreach (var c in part)
                {
                    if (c < 0x21 || c > 0x7E)
                    {
                        return false;
                    }

                    switch (c)
                    {
                        case '#':
                        case '*':
                        case ',':
                        case '?':
                        case '[':
                        case ']':
                        case '{':
                        case '}':
                            return false;
                    }
                }
            }

            if (parts[0] == "sys" || parts[0] == "oscdesk")
            {
                return false; // 受け口とブリッジの内部のアドレスは操作の候補にしない
            }

            segments = parts;
            return true;
        }

        /// <summary>操作から TYPE・ACCESS・RANGE を決める。OSCQuery で表せない型なら false。</summary>
        public static bool TryDescribe(OscOperation operation, out string type, out int access, out IReadOnlyList<OscQueryRange> range)
        {
            type = null;
            range = null;
            access = 0;
            if (operation == null)
            {
                return false;
            }

            access = operation.IsCommand ? 2 : 3;
            if (operation.ValueType != null)
            {
                return TryDescribeComposite(operation.ValueType, out type, out range);
            }

            switch (operation.Kind)
            {
                case OscBindingKind.Trigger:
                    access = 2;
                    return true;
                case OscBindingKind.Int:
                    type = "i";
                    break;
                case OscBindingKind.Float:
                    type = "f";
                    break;
                case OscBindingKind.String:
                    type = "s";
                    break;
                case OscBindingKind.Bool:
                    type = "T";
                    break;
                default:
                    return false;
            }

            var one = ScalarRange(operation.Kind, operation.Min, operation.Max, operation.Options);
            range = one != null ? new[] { one } : null;
            return true;
        }

        private static bool TryDescribeComposite(OscValueType valueType, out string type, out IReadOnlyList<OscQueryRange> range)
        {
            type = null;
            range = null;
            if (!valueType.IsFlatRecord || valueType.Fields.Count == 0)
            {
                return false;
            }

            var tags = new StringBuilder();
            var ranges = new List<OscQueryRange>(valueType.Fields.Count);
            var any = false;
            foreach (var field in valueType.Fields)
            {
                OscBindingKind kind;
                switch (field.Type.Kind)
                {
                    case OscValueTypeKind.Int: kind = OscBindingKind.Int; tags.Append('i'); break;
                    case OscValueTypeKind.Float: kind = OscBindingKind.Float; tags.Append('f'); break;
                    case OscValueTypeKind.String: kind = OscBindingKind.String; tags.Append('s'); break;
                    case OscValueTypeKind.Bool: kind = OscBindingKind.Bool; tags.Append('T'); break;
                    default: return false;
                }

                var one = ScalarRange(kind, field.Type.Min, field.Type.Max, field.Type.Options);
                any |= one != null;
                ranges.Add(one ?? new OscQueryRange(null, null, null));
            }

            type = tags.ToString();
            range = any ? ranges : null;
            return true;
        }

        private static OscQueryRange ScalarRange(OscBindingKind kind, float? min, float? max, IReadOnlyList<string> options)
        {
            if (kind == OscBindingKind.Bool)
            {
                return null;
            }

            if (options != null && options.Count > 0)
            {
                var vals = new List<object>(options.Count);
                foreach (var option in options)
                {
                    switch (kind)
                    {
                        case OscBindingKind.Int:
                            if (int.TryParse(option, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) vals.Add(i);
                            break;
                        case OscBindingKind.Float:
                            if (float.TryParse(option, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && !float.IsNaN(f) && !float.IsInfinity(f)) vals.Add(f);
                            break;
                        default:
                            vals.Add(CleanDescription(option));
                            break;
                    }
                }

                if (vals.Count > 0)
                {
                    return new OscQueryRange(null, null, vals);
                }
            }

            var low = Number(kind, min);
            var high = Number(kind, max);
            return low == null && high == null ? null : new OscQueryRange(low, high, null);
        }

        private static object Number(OscBindingKind kind, float? value)
        {
            if (!value.HasValue || float.IsNaN(value.Value) || float.IsInfinity(value.Value))
            {
                return null;
            }

            // int32 の範囲は double で比べる(float の int.MaxValue は 2147483648 に丸まるため)
            var rounded = Math.Round((double)value.Value);
            if (kind == OscBindingKind.Int && rounded >= int.MinValue && rounded <= int.MaxValue && Math.Abs(value.Value - rounded) < 1e-6)
            {
                return (int)rounded;
            }

            return value.Value;
        }

        /// <summary>ノード(と子孫)の JSON。</summary>
        public string ToJson(OscQueryNode node)
        {
            var builder = new StringBuilder();
            WriteNode(builder, node ?? Root);
            return builder.ToString();
        }

        /// <summary>
        /// 属性 1 つのクエリ(<c>?TYPE</c> など)の JSON(<c>{"TYPE":"f"}</c>)。ノードがその属性を持たないか、対応していない属性なら null
        /// (HTTP では 204 で返す。HID-484 §6 の 1)。
        /// </summary>
        public static string AttributeJson(OscQueryNode node, string attribute)
        {
            if (node == null || attribute == null)
            {
                return null;
            }

            var builder = new StringBuilder();
            builder.Append('{');
            switch (attribute)
            {
                case "FULL_PATH":
                    builder.Append("\"FULL_PATH\":");
                    OscQueryJson.WriteString(builder, node.FullPath);
                    break;
                case "CONTENTS":
                    if (node.ChildCount == 0) return null;
                    builder.Append("\"CONTENTS\":");
                    WriteContents(builder, node);
                    break;
                case "TYPE":
                    if (node.Type == null) return null;
                    builder.Append("\"TYPE\":");
                    OscQueryJson.WriteString(builder, node.Type);
                    break;
                case "ACCESS":
                    if (!node.IsMethod) return null;
                    builder.Append("\"ACCESS\":").Append(node.Access.ToString(CultureInfo.InvariantCulture));
                    break;
                case "RANGE":
                    if (node.Range == null) return null;
                    builder.Append("\"RANGE\":");
                    WriteRange(builder, node.Range);
                    break;
                case "DESCRIPTION":
                    if (node.Description.Length == 0) return null;
                    builder.Append("\"DESCRIPTION\":");
                    OscQueryJson.WriteString(builder, node.Description);
                    break;
                default:
                    return null; // VALUE は持たない。ほかの属性(TAGS・UNIT など)も持たない
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static void WriteNode(StringBuilder builder, OscQueryNode node)
        {
            builder.Append("{\"FULL_PATH\":");
            OscQueryJson.WriteString(builder, node.FullPath);
            if (node.Type != null)
            {
                builder.Append(",\"TYPE\":");
                OscQueryJson.WriteString(builder, node.Type);
            }

            builder.Append(",\"ACCESS\":").Append(node.IsMethod ? node.Access.ToString(CultureInfo.InvariantCulture) : "0");
            if (node.Range != null)
            {
                builder.Append(",\"RANGE\":");
                WriteRange(builder, node.Range);
            }

            if (node.Description.Length > 0)
            {
                builder.Append(",\"DESCRIPTION\":");
                OscQueryJson.WriteString(builder, node.Description);
            }

            if (node.ChildCount > 0)
            {
                builder.Append(",\"CONTENTS\":");
                WriteContents(builder, node);
            }

            builder.Append('}');
        }

        private static void WriteContents(StringBuilder builder, OscQueryNode node)
        {
            builder.Append('{');
            var first = true;
            foreach (var child in node.Contents)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                OscQueryJson.WriteString(builder, child.Name);
                builder.Append(':');
                WriteNode(builder, child);
            }

            builder.Append('}');
        }

        internal static void WriteRange(StringBuilder builder, IReadOnlyList<OscQueryRange> range)
        {
            builder.Append('[');
            for (var i = 0; i < range.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                range[i].Write(builder);
            }

            builder.Append(']');
        }

        /// <summary>
        /// 2 つの木の違い。増えた段・消えた段はいちばん上の段だけを挙げ(子孫は含まれる)、属性が変わったノードは PATH_CHANGED にする。
        /// 並びは消えたもの → 増えたもの → 変わったもの。maxChanges を超えたら、根の PATH_CHANGED 1 件にまとめる
        /// (受け側は種類を問わず木を取り直すので、細かく知らせる意味が薄い。HID-484 §6 の 5)。
        /// </summary>
        public static List<OscQueryPathChange> Diff(OscQueryTree before, OscQueryTree after, int maxChanges = 256)
        {
            before = before ?? Empty();
            after = after ?? Empty();
            var removed = new List<string>();
            var added = new List<string>();
            var changed = new List<string>();
            foreach (var pair in before.byPath)
            {
                if (!after.byPath.TryGetValue(pair.Key, out var now))
                {
                    // 親も消えたなら親の PATH_REMOVED に含まれる(根は消えない)
                    if (after.byPath.ContainsKey(ParentPath(pair.Key)))
                    {
                        removed.Add(pair.Key);
                    }
                }
                else if (!string.Equals(pair.Value.Signature(), now.Signature(), StringComparison.Ordinal))
                {
                    changed.Add(pair.Key);
                }
            }

            foreach (var key in after.byPath.Keys)
            {
                if (!before.byPath.ContainsKey(key) && before.byPath.ContainsKey(ParentPath(key)))
                {
                    added.Add(key);
                }
            }

            removed.Sort(StringComparer.Ordinal);
            added.Sort(StringComparer.Ordinal);
            changed.Sort(StringComparer.Ordinal);
            var result = new List<OscQueryPathChange>(removed.Count + added.Count + changed.Count);
            if (removed.Count + added.Count + changed.Count > maxChanges)
            {
                result.Add(new OscQueryPathChange(OscQueryPathChangeKind.Changed, "/"));
                return result;
            }

            foreach (var path in removed) result.Add(new OscQueryPathChange(OscQueryPathChangeKind.Removed, path));
            foreach (var path in added) result.Add(new OscQueryPathChange(OscQueryPathChangeKind.Added, path));
            foreach (var path in changed) result.Add(new OscQueryPathChange(OscQueryPathChangeKind.Changed, path));
            return result;
        }
    }

    /// <summary>OSCQuery の HOST_INFO。</summary>
    public static class OscQueryHostInfo
    {
        /// <summary>
        /// HOST_INFO の JSON。WS_PORT と EXTENSIONS を明示し、OSC_IP は省く(ブリッジは HTTP を取ったアドレスへ送る。HID-484 §6 の 4・7)。
        /// 起動ごとの ID を <see cref="OscBootId.HostInfoKey"/> に載せる。
        /// </summary>
        public static string ToJson(string name, int oscPort, int wsPort, string bootId)
        {
            var builder = new StringBuilder();
            builder.Append("{\"NAME\":");
            OscQueryJson.WriteString(builder, name ?? string.Empty);
            builder.Append(",\"EXTENSIONS\":{")
                .Append("\"ACCESS\":true,\"VALUE\":false,\"RANGE\":true,\"DESCRIPTION\":true,\"TAGS\":false,\"EXTENDED_TYPE\":false,")
                .Append("\"UNIT\":false,\"CRITICAL\":false,\"CLIPMODE\":false,\"OVERLOADS\":false,")
                .Append("\"LISTEN\":true,\"PATH_CHANGED\":true,\"PATH_RENAMED\":false,\"PATH_ADDED\":true,\"PATH_REMOVED\":true}");
            builder.Append(",\"OSC_PORT\":").Append(oscPort.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"OSC_TRANSPORT\":\"UDP\"");
            builder.Append(",\"WS_PORT\":").Append(wsPort.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"").Append(OscBootId.HostInfoKey).Append("\":");
            OscQueryJson.WriteString(builder, bootId ?? string.Empty);
            builder.Append('}');
            return builder.ToString();
        }
    }
}
