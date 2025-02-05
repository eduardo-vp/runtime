// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

using Internal.IntrinsicSupport;
using Internal.Runtime.CompilerServices;

namespace System.Collections.Generic
{
    public abstract partial class Comparer<T> : IComparer, IComparer<T>
    {
        [Intrinsic]
        private static Comparer<T> Create()
        {
            // The compiler will overwrite the Create method with optimized
            // instantiation-specific implementation.
            // This body serves as a fallback when instantiation-specific implementation is unavailable.
            // If that happens, the compiler ensures we generate data structures to make the fallback work
            // when this method is compiled.
            return Unsafe.As<Comparer<T>>(ComparerHelpers.GetComparer(typeof(T).TypeHandle));
        }

        public static Comparer<T> Default { [Intrinsic] get; } = Create();
    }

    internal sealed partial class EnumComparer<T> : Comparer<T> where T : struct, Enum
    {
        public override int Compare(T x, T y)
        {
            return ComparerHelpers.EnumOnlyCompare(x, y);
        }
    }
}
