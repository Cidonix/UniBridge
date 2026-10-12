using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniBridge.SerializedNumericRegression
{
    [Serializable]
    public struct NumericBlock
    {
        public int IntValue;
        public uint UIntValue;
        public long LongValue;
        public ulong ULongValue;
        public float FloatValue;
        public double DoubleValue;
        public static NumericBlock Initial => new NumericBlock { IntValue = -1234567890, UIntValue = 4000000000U,
            LongValue = 9007199254740993L, ULongValue = ulong.MaxValue, FloatValue = 1.2345678f, DoubleValue = 1.0000000000000002d };
    }

    public sealed class SerializedNumericComponent : MonoBehaviour
    {
        public int IntValue = -1234567890;
        public uint UIntValue = 4000000000U;
        public long LongValue = 9007199254740993L;
        public ulong ULongValue = ulong.MaxValue;
        public float FloatValue = 1.2345678f;
        public double DoubleValue = 1.0000000000000002d;
        public NumericBlock Nested = NumericBlock.Initial;
        public List<NumericBlock> Blocks = new List<NumericBlock> { NumericBlock.Initial };
        public long[] Longs = { 9007199254740993L, long.MinValue };
        public ulong[] ULongs = { ulong.MaxValue, 9223372036854775808UL };
        public double[] Doubles = { 1.0000000000000002d, 1.2345678901234567d };
    }
}
