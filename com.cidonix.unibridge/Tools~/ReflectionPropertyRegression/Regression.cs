using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MathRegression
{
    static class Regression
    {
        static readonly List<object> Cases = new List<object>();
        static int failures;
        static void Check(string name, Func<bool> check)
        {
            bool passed = false; string error = null;
            try { passed = check(); } catch (Exception e) { error = e.ToString(); }
            Cases.Add(new { name, passed, error }); if (!passed) failures++;
            Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
        }
        static bool Applied(Root root, string path, JToken token)
        {
            var result = ManageGameObject.Write(root, path, token);
            return result.Written && result.ReadbackAvailable && ManageGameObject.Equal(result.ExpectedValue, result.ActualValue);
        }
        static bool Rejected(Root root, string path, JToken token)
        {
            var before = root.Node.Value; var result = ManageGameObject.Write(root, path, token);
            return !result.Attempted && !result.Written && result.Error != null && root.Node.Value == before;
        }
        public static int Main(string[] args)
        {
            Check("nested-property-current-readback", () => { var r = new Root(); return Applied(r, "Node.Value", new JValue(3)) && r.Node.Value == 3; });
            Check("nested-field-case-insensitive", () => { var r = new Root(); return Applied(r, "node.field", new JValue(4)) && r.Node.Field == 4; });
            Check("list-nested-member", () => { var r = new Root(); return Applied(r, "Nodes[0].Value", new JValue(5)) && r.Nodes[0].Value == 5; });
            Check("array-nested-member", () => { var r = new Root(); return Applied(r, "NodeArray[0].Value", new JValue(6)) && r.NodeArray[0].Value == 6; });
            Check("terminal-array-index", () => { var r = new Root(); return Applied(r, "Numbers[0]", new JValue(7)) && r.Numbers[0] == 7; });
            Check("terminal-list-index", () => { var r = new Root(); return Applied(r, "NumberList[0]", new JValue(8)) && r.NumberList[0] == 8; });
            Check("replace-null-list-element", () => { var r = new Root(); r.Nodes[0] = null; var w = ManageGameObject.Write(r, "Nodes[0]", JObject.Parse("{\"Value\":9}")); return w.Written && w.ReadbackAvailable && ReferenceEquals(w.ActualValue, r.Nodes[0]) && r.Nodes[0].Value == 9; });
            Check("whole-struct-replacement", () => { var r = new Root(); return Applied(r, "Vector", new JArray(1, 2, 3)) && r.Vector.z == 3; });
            Check("boxed-struct-write-rejected", () => { var r = new Root(); return Rejected(r, "Vector.x", new JValue(9)) && r.Vector.x == 0; });
            Check("clamp-mismatch-retains-actual", () => { var r = new Root(); var w = ManageGameObject.Write(r, "Node.Clamped", new JValue(3)); return w.Attempted && w.Written && w.ReadbackAvailable && (float)w.ActualValue == 1f && !ManageGameObject.Equal(w.ExpectedValue, w.ActualValue) && r.Node.Calls == 1; });
            Check("setter-throws-after-changing-truthful-no-replay", () => { var r = new Root(); var w = ManageGameObject.Write(r, "Node.ThrowAfter", new JValue(9)); return w.Attempted && !w.Written && w.ReadbackAvailable && (int)w.ActualValue == 9 && r.Node.Calls == 1; });
            Check("setter-throws-before-changing-truthful", () => { var r = new Root(); var w = ManageGameObject.Write(r, "Node.ThrowBefore", new JValue(9)); return w.Attempted && !w.Written && w.ReadbackAvailable && (int)w.ActualValue == 0 && r.Node.Calls == 1; });
            Check("replaced-parent-readback-current-not-stale", () => { var r = new Root(); var old = r.Node; old.Replace = () => r.Node = new Node { Value = 99 }; var w = ManageGameObject.Write(r, "Node.Switch", new JValue(3)); return w.Written && w.ReadbackAvailable && (int)w.ActualValue == 99 && old.Value == 3 && !ManageGameObject.Equal(w.ExpectedValue, w.ActualValue); });
            Check("write-only-leaf-truthful-unverifiable", () => { var r = new Root(); var w = ManageGameObject.Write(r, "Node.WriteOnly", new JValue(3)); return w.Written && !w.ReadbackAvailable && r.Node.Value == 3 && w.Error != null; });
            Check("getter-exception-after-write-truthful", () => { var r = new Root(); var w = ManageGameObject.Write(r, "Node.BadGetter", new JValue(3)); return w.Written && !w.ReadbackAvailable && r.Node.Value == 3; });
            Check("missing-leaf-rejected", () => Rejected(new Root(), "Node.Missing", new JValue(1)));
            Check("null-parent-rejected", () => Rejected(new Root(), "Absent.Value", new JValue(1)));
            Check("null-list-node-rejected", () => { var r = new Root(); r.Nodes[0] = null; return Rejected(r, "Nodes[0].Value", new JValue(1)); });
            Check("out-of-range-rejected", () => Rejected(new Root(), "Nodes[9].Value", new JValue(1)));
            Check("negative-index-rejected", () => Rejected(new Root(), "Nodes[-1].Value", new JValue(1)));
            Check("repeated-index-rejected", () => Rejected(new Root(), "Nodes[0][0].Value", new JValue(1)));
            Check("unmatched-bracket-rejected", () => Rejected(new Root(), "Nodes[0.Value", new JValue(1)));
            Check("empty-segment-rejected", () => Rejected(new Root(), "Node..Value", new JValue(1)));
            Check("dictionary-index-remains-unsupported", () => Rejected(new Root(), "Dictionary[0].Value", new JValue(1)));
            Check("readonly-leaf-rejected", () => Rejected(new Root(), "ReadOnly", new JValue(1)));
            Check("readonly-collection-item-rejected", () => Rejected(new Root(), "ReadOnlyNodes[0]", JObject.Parse("{\"Value\":1}")));
            Check("malformed-value-rejected-before-setter", () => { var r = new Root(); return Rejected(r, "Node.Value", new JObject { ["bad"] = 1 }) && r.Node.Calls == 0; });
            Check("direct-read-and-nested-read-share-resolver", () => { var r = new Root(); r.Node.Value = 12; object value; return ManageGameObject.Read(r, "Node.Value", out value) && (int)value == 12; });
            File.WriteAllText(args[0], JsonConvert.SerializeObject(new { passed = failures == 0, failures, cases = Cases,
                proof = "Whole candidate resolver and exact conversion/readback comparison bodies; real Unity SDK math types outside Editor. Material endpoints compile against SDK; physical material calls require native gate." }, Formatting.Indented));
            return failures == 0 ? 0 : 1;
        }
        sealed class Root
        {
            public Node Node { get; set; } = new Node();
            public Node Absent { get; set; }
            public List<Node> Nodes { get; set; } = new List<Node> { new Node() };
            public Node[] NodeArray { get; set; } = new[] { new Node() };
            public float[] Numbers { get; set; } = new[] { 0f };
            public List<float> NumberList { get; set; } = new List<float> { 0f };
            public Dictionary<int, Node> Dictionary { get; set; } = new Dictionary<int, Node> { [0] = new Node() };
            public ReadOnlyCollection<Node> ReadOnlyNodes { get; set; } = new List<Node> { new Node() }.AsReadOnly();
            public Vector3 Vector { get; set; }
            public int ReadOnly { get { return 1; } }
        }
        sealed class Node
        {
            public int Value { get; set; }
            public int Field;
            public int Calls;
            public Action Replace;
            float clamped;
            int after;
            public float Clamped { get { return clamped; } set { Calls++; clamped = Math.Max(0, Math.Min(1, value)); } }
            public int ThrowAfter { get { return after; } set { Calls++; after = value; throw new InvalidOperationException("after"); } }
            public int ThrowBefore { get { return 0; } set { Calls++; throw new InvalidOperationException("before"); } }
            public int Switch { get { return Value; } set { Value = value; Replace(); } }
            public int WriteOnly { set { Value = value; } }
            public int BadGetter { get { throw new InvalidOperationException("getter"); } set { Value = value; } }
        }
    }
}
