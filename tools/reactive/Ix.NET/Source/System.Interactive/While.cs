﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information. 

using System.Collections.Generic;

namespace System.Linq
{
    public static partial class EnumerableEx
    {
        /// <summary>
        ///     Generates an enumerable sequence by repeating a source sequence as long as the given loop condition holds.
        /// </summary>
        /// <typeparam name="TResult">Result sequence element type.</typeparam>
        /// <param name="condition">Loop condition.</param>
        /// <param name="source">Sequence to repeat while the condition evaluates true.</param>
        /// <returns>Sequence generated by repeating the given sequence while the condition evaluates to true.</returns>
        public static IEnumerable<TResult> While<TResult>(Func<bool> condition, IEnumerable<TResult> source)
        {
            if (condition == null)
            {
                throw new ArgumentNullException(nameof(condition));
            }

            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return WhileCore(condition, source)
                .Concat();
        }

        private static IEnumerable<IEnumerable<TSource>> WhileCore<TSource>(Func<bool> condition, IEnumerable<TSource> source)
        {
            while (condition())
            {
                yield return source;
            }
        }
    }
}