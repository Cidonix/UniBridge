// Owned test-project fixture template. Tools~ is not imported into the host.
// A native runner imports a UUID-owned copy, waits for readiness, then removes
// its unchanged source/meta pair after the physical MCP setter qualification.
using UnityEngine;

namespace UniBridgeQualification
{
    public sealed class UnityValuePropertyProbe : MonoBehaviour
    {
        // Nonserialized properties deliberately exercise the reflection setter,
        // unlike serialized fields, which use SerializedPropertyPatcher first.
        public Matrix4x4 MatrixProperty { get; set; }
        public Vector4 Vector4Property { get; set; }
        public Vector3 Vector3Property { get; set; }
        public Bounds BoundsProperty { get; set; }
        public Rect RectProperty { get; set; }
        public Quaternion QuaternionProperty { get; set; }
        public Color ColorProperty { get; set; }
    }
}
