using System;
using UnityEngine;
public class SettingsReadProbe : ScriptableObject
{
    public long WideSigned = 9007199254740993;
    public ulong WideUnsigned = ulong.MaxValue;
    public double Precise = 1.000000000001;
    public string Text = "ab\U0001f680cd";
    public int[] Numbers = { 11, 22, 33, 44 };
    public Vector4 Vector = new(1, 2, 3, 4);
    public Texture2D TypedTexture;
    public Node Nested = new();
    [Serializable] public class Node { public int Value = 19; public string Name = "Nested name"; }
}
