// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;
using Internal.Runtime.CompilerServices;

namespace System.Collections.Generic
{
    internal struct InvariantArray<T>
    {
        private T[] _inner;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InvariantArray(int length)
        {
            _inner = new T[length];
        }

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref Unsafe.AsRef(in _inner[index]);
        }

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _inner.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] AsArray()
        {
            return _inner;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator T[] (InvariantArray<T> ia)
        {
            return ia.AsArray();
        }
    }
}
