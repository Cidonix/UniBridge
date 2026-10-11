// Type-identity adapters for the JSON/lifecycle suite. Mathematical behavior is
// qualified separately against real Unity SDK assemblies and a native Editor.
namespace UnityEngine
{
    public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } }
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    public struct Vector4 { public float x, y, z, w; public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; } }
    public struct Quaternion { public float x, y, z, w; public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; } }
    public struct Color { public float r, g, b, a; public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; } }
    public struct Rect { public float x, y, width, height; public Rect(float x, float y, float width, float height) { this.x = x; this.y = y; this.width = width; this.height = height; } }
    public struct Bounds { public Vector3 center, size; public Bounds(Vector3 center, Vector3 size) { this.center = center; this.size = size; } }
    public struct Matrix4x4
    {
        float[] values;
        public float this[int row, int column]
        {
            get { return values == null ? 0 : values[row * 4 + column]; }
            set { if (values == null) values = new float[16]; values[row * 4 + column] = value; }
        }
    }
}
