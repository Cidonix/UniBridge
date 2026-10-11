// Qualification template, imported only into a UUID-owned test folder.
using System.Collections.Generic;
using UnityEngine;
namespace UniBridgeQualification
{
    public sealed class ReflectionPropertyProbe : MonoBehaviour
    {
        public sealed class Node
        {
            public int Value { get; set; }
            public int Field;
            float clamped;
            public float Clamped { get { return clamped; } set { clamped = Mathf.Clamp01(value); } }
        }
        // Properties are intentionally nonserialized, exercising the reflection fallback.
        public Node Nested { get; set; } = new Node();
        public Node Absent { get; set; }
        public List<Node> Nodes { get; set; } = new List<Node> { new Node() };
        public Node[] NodeArray { get; set; } = new[] { new Node() };
        public Material OwnedMaterial { get; set; }
    }
}
