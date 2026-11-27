using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace OscDesk.Receiver.Tests
{
    // Registry の登録から OSCQuery の名前空間の木を作る規則(HID-490・HID-484 §5・§6)
    public sealed class OscQueryNamespaceTests
    {
        private static OscRegistryEntry Entry(string address, OscOperation operation, string targetDescription = null)
        {
            return new OscRegistryEntry(address, operation, null, null, null, targetDescription);
        }

        private static OscOperation Op(OscBindingKind kind)
        {
            return new OscOperation("x", kind, _ => null);
        }

        [Test]
        public void Scalar_kinds_map_to_type_tags_and_access()
        {
            var tree = OscQueryTree.Build(new[]
            {
                Entry("/mix/level", Op(OscBindingKind.Float)),
                Entry("/mix/count", Op(OscBindingKind.Int)),
                Entry("/mix/name", Op(OscBindingKind.String)),
                Entry("/mix/mute", Op(OscBindingKind.Bool)),
                Entry("/cue/go", Op(OscBindingKind.Trigger)),
                Entry("/fx/spawner/spawn", Op(OscBindingKind.String).AsCommand()),
            });

            Assert.That(tree.Skipped, Is.Empty);
            Assert.That(Node(tree, "/mix/level").Type, Is.EqualTo("f"));
            Assert.That(Node(tree, "/mix/count").Type, Is.EqualTo("i"));
            Assert.That(Node(tree, "/mix/name").Type, Is.EqualTo("s"));
            Assert.That(Node(tree, "/mix/mute").Type, Is.EqualTo("T"));
            Assert.That(Node(tree, "/mix/level").Access, Is.EqualTo(3));
            Assert.That(Node(tree, "/cue/go").Type, Is.Null, "きっかけは TYPE を持たない");
            Assert.That(Node(tree, "/cue/go").Access, Is.EqualTo(2));
            Assert.That(Node(tree, "/fx/spawner/spawn").Type, Is.EqualTo("s"));
            Assert.That(Node(tree, "/fx/spawner/spawn").Access, Is.EqualTo(2), "命令は送るだけ(保持して送り直さない)");
            Assert.That(Node(tree, "/mix").IsMethod, Is.False);
        }

        [Test]
        public void Options_become_vals_and_numeric_bounds_become_min_max()
        {
            var tree = OscQueryTree.Build(new[]
            {
                Entry("/mb/segment", Op(OscBindingKind.String).WithOptions("(none)", "Intro", "日本語")),
                Entry("/mix/level", Op(OscBindingKind.Float).WithRange(0f, 1.5f)),
                Entry("/mix/count", Op(OscBindingKind.Int).WithRange(0f, 10f)),
                Entry("/mix/mode", Op(OscBindingKind.Int).WithOptions("1", "2", "x")),
            });

            Assert.That(OscQueryTree.AttributeJson(Node(tree, "/mb/segment"), "RANGE"), Is.EqualTo("{\"RANGE\":[{\"VALS\":[\"(none)\",\"Intro\",\"日本語\"]}]}"));
            Assert.That(OscQueryTree.AttributeJson(Node(tree, "/mix/level"), "RANGE"), Is.EqualTo("{\"RANGE\":[{\"MIN\":0,\"MAX\":1.5}]}"));
            Assert.That(OscQueryTree.AttributeJson(Node(tree, "/mix/count"), "RANGE"), Is.EqualTo("{\"RANGE\":[{\"MIN\":0,\"MAX\":10}]}"));
            Assert.That(OscQueryTree.AttributeJson(Node(tree, "/mix/mode"), "RANGE"), Is.EqualTo("{\"RANGE\":[{\"VALS\":[1,2]}]}"), "数でない選択肢は落とす");
        }

        [Test]
        public void Rows_use_ascii_id_paths_and_carry_display_names_in_description()
        {
            var tree = OscQueryTree.Build(new[]
            {
                Entry("/member/m01/active", Op(OscBindingKind.Bool).WithLabel("出演"), "山田"),
                Entry("/member/m01/slot", Op(OscBindingKind.Int), "山田"),
                Entry("/member/m02/active", Op(OscBindingKind.Bool), "佐藤"),
            });

            Assert.That(Node(tree, "/member/m01").Description, Is.EqualTo("山田"));
            Assert.That(Node(tree, "/member/m02").Description, Is.EqualTo("佐藤"));
            Assert.That(Node(tree, "/member/m01/active").Description, Is.EqualTo("出演"));
            Assert.That(Node(tree, "/member").Description, Is.Empty);
        }

        [Test]
        public void Non_ascii_pattern_system_and_unrepresentable_entries_are_skipped()
        {
            var tree = OscQueryTree.Build(new[]
            {
                Entry("/fx/日本/stop", Op(OscBindingKind.Trigger)),
                Entry("/fx/a b/stop", Op(OscBindingKind.Trigger)),
                Entry("/fx/*/stop", Op(OscBindingKind.Trigger)),
                Entry("/fx//stop", Op(OscBindingKind.Trigger)),
                Entry("/sys/ping", Op(OscBindingKind.Trigger)),
                Entry("/list/names", Op(OscBindingKind.Strings)),
                Entry("/member/row", Op(OscBindingKind.Record)),
                Entry("/ok/go", Op(OscBindingKind.Trigger)),
            });

            Assert.That(tree.Skipped.Select(s => s.Address), Is.EqualTo(new[]
            {
                "/fx/日本/stop", "/fx/a b/stop", "/fx/*/stop", "/fx//stop", "/sys/ping", "/list/names", "/member/row"
            }));
            Assert.That(tree.Skipped.Last().Reason, Is.EqualTo(OscQueryTree.ReasonUnsupportedType));
            Assert.That(tree.TryFind("/ok/go", out _), Is.True);
            Assert.That(tree.TryFind("/fx", out _), Is.False);
        }

        [Test]
        public void Json_has_full_path_contents_and_omits_value()
        {
            var tree = OscQueryTree.Build(new[]
            {
                Entry("/mix/level", Op(OscBindingKind.Float).WithRange(0f, 1f).WithLabel("音量")),
                Entry("/cue/go", Op(OscBindingKind.Trigger)),
            });

            Assert.That(tree.ToJson(tree.Root), Is.EqualTo(
                "{\"FULL_PATH\":\"/\",\"ACCESS\":0,\"CONTENTS\":{"
                + "\"cue\":{\"FULL_PATH\":\"/cue\",\"ACCESS\":0,\"CONTENTS\":{\"go\":{\"FULL_PATH\":\"/cue/go\",\"ACCESS\":2}}},"
                + "\"mix\":{\"FULL_PATH\":\"/mix\",\"ACCESS\":0,\"CONTENTS\":{\"level\":{\"FULL_PATH\":\"/mix/level\",\"TYPE\":\"f\",\"ACCESS\":3,"
                + "\"RANGE\":[{\"MIN\":0,\"MAX\":1}],\"DESCRIPTION\":\"音量\"}}}}}"));
        }

        [Test]
        public void Attribute_queries_return_null_for_missing_or_unsupported_attributes()
        {
            var tree = OscQueryTree.Build(new[] { Entry("/cue/go", Op(OscBindingKind.Trigger)) });
            var go = Node(tree, "/cue/go");
            Assert.That(OscQueryTree.AttributeJson(go, "FULL_PATH"), Is.EqualTo("{\"FULL_PATH\":\"/cue/go\"}"));
            Assert.That(OscQueryTree.AttributeJson(go, "ACCESS"), Is.EqualTo("{\"ACCESS\":2}"));
            Assert.That(OscQueryTree.AttributeJson(go, "TYPE"), Is.Null);
            Assert.That(OscQueryTree.AttributeJson(go, "VALUE"), Is.Null, "値を持たないので VALUE は無い");
            Assert.That(OscQueryTree.AttributeJson(go, "UNIT"), Is.Null);
            Assert.That(OscQueryTree.AttributeJson(Node(tree, "/cue"), "CONTENTS"), Is.EqualTo("{\"CONTENTS\":{\"go\":{\"FULL_PATH\":\"/cue/go\",\"ACCESS\":2}}}"));
            Assert.That(tree.TryFind("/cue/", out var cue), Is.True, "末尾の / は無視する");
            Assert.That(cue.FullPath, Is.EqualTo("/cue"));
        }

        [Test]
        public void Diff_reports_topmost_added_and_removed_paths_and_changed_attributes()
        {
            var before = OscQueryTree.Build(new[]
            {
                Entry("/member/m01/active", Op(OscBindingKind.Bool), "山田"),
                Entry("/mb/segment", Op(OscBindingKind.String).WithOptions("a")),
            });
            var after = OscQueryTree.Build(new[]
            {
                Entry("/member/m02/active", Op(OscBindingKind.Bool), "佐藤"),
                Entry("/member/m02/slot", Op(OscBindingKind.Int), "佐藤"),
                Entry("/mb/segment", Op(OscBindingKind.String).WithOptions("a", "b")),
            });

            var changes = OscQueryTree.Diff(before, after).Select(c => c.ToString()).ToList();
            Assert.That(changes, Is.EqualTo(new[] { "PATH_REMOVED /member/m01", "PATH_ADDED /member/m02", "PATH_CHANGED /mb/segment" }));
            Assert.That(OscQueryTree.Diff(after, after), Is.Empty);
        }

        [Test]
        public void Diff_beyond_the_limit_collapses_into_one_root_change()
        {
            var many = new List<OscRegistryEntry>();
            for (var i = 0; i < 10; i++)
            {
                many.Add(Entry("/row" + i + "/go", Op(OscBindingKind.Trigger)));
            }

            var changes = OscQueryTree.Diff(OscQueryTree.Empty(), OscQueryTree.Build(many), 5);
            Assert.That(changes.Select(c => c.ToString()), Is.EqualTo(new[] { "PATH_CHANGED /" }));
        }

        [Test]
        public void Path_change_json_is_the_oscquery_command()
        {
            Assert.That(new OscQueryPathChange(OscQueryPathChangeKind.Added, "/a/b").ToJson(), Is.EqualTo("{\"COMMAND\":\"PATH_ADDED\",\"DATA\":\"/a/b\"}"));
        }

        [Test]
        public void Host_info_names_ws_port_extensions_and_boot_id_without_osc_ip()
        {
            var json = OscQueryHostInfo.ToJson("Stage (7090)", 7090, 54321, "abc");
            StringAssert.StartsWith("{\"NAME\":\"Stage (7090)\",\"EXTENSIONS\":{", json);
            StringAssert.Contains("\"PATH_ADDED\":true", json);
            StringAssert.Contains("\"VALUE\":false", json);
            StringAssert.Contains("\"OSC_PORT\":7090", json);
            StringAssert.Contains("\"OSC_TRANSPORT\":\"UDP\"", json);
            StringAssert.Contains("\"WS_PORT\":54321", json);
            StringAssert.EndsWith(",\"X_OSCDESK_BOOT_ID\":\"abc\"}", json);
            Assert.That(json, Does.Not.Contain("OSC_IP"));
        }

        [Test]
        public void Namespace_follows_the_registry_with_target_display_names()
        {
            var registry = new OscRegistry();
            var operation = new OscOperation("active", OscBindingKind.Bool, _ => null);
            registry.Register(new OscRegistryEntry("/member/m01/active", operation, null, "member", "m01", "山田"));
            var tree = OscQueryTree.Build(registry.Ordered);
            Assert.That(Node(tree, "/member/m01").Description, Is.EqualTo("山田"));
        }

        private static OscQueryNode Node(OscQueryTree tree, string path)
        {
            Assert.That(tree.TryFind(path, out var node), Is.True, path);
            return node;
        }
    }
}
