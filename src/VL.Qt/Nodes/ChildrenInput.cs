using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Styles;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Turns the children input (the pin group, a <see cref="Spread{T}"/> that VL may rebuild every frame, or the
    /// Sequence input of the Spectral nodes) into a stable <see cref="ImmutableArray{T}"/>: unconnected pins (null) and
    /// unknown <see cref="IElement"/> implementations are dropped, an <see cref="IStylable"/> becomes a <see cref="StylableElement"/> (the instance is kept while the
    /// stylable stays the same), and the same array instance is returned as long as the children are reference equal
    /// element by element. Since <see cref="ImmutableArray{T}"/> equality is reference equality of its backing array, the
    /// result can go straight into a node's <c>Props</c> record struct for change detection. No allocation if nothing
    /// changed and the input is a Spread of <see cref="IElement"/> or <see cref="Element"/>. One instance per node, main
    /// thread only.
    /// </summary>
    internal sealed class ChildrenInput
    {
        private readonly List<IElement?> _buffer = new();
        private object? _lastInput;
        private ImmutableArray<Element> _current = ImmutableArray<Element>.Empty;

        /// <summary>Current children (never default).</summary>
        public ImmutableArray<Element> Current => _current;

        /// <summary>Number of arrays built so far (for tests).</summary>
        public int Built { get; private set; }

        public ImmutableArray<Element> Update(IEnumerable<IElement?>? input)
        {
            // Spreads are immutable: the same instance means the same children.
            if (input is Spread<IElement?> or Spread<Element> && ReferenceEquals(input, _lastInput))
                return _current;
            _lastInput = input;

            switch (input)
            {
                case null:
                    Apply(ReadOnlySpan<IElement?>.Empty);
                    break;
                case Spread<IElement?> spread:
                    Apply(spread.AsSpan());
                    break;
                // A Spread<Element> reaches a Sequence<IElement> pin through covariance; read it without copying.
                case Spread<Element> spread:
                    Apply(spread.AsSpan());
                    break;
                default:
                    // Other sequences (a Spread of a self-made widget type, a lazy sequence) may change behind the
                    // same instance: copy into the reused buffer and compare.
                    try
                    {
                        Fill(_buffer, input);
                        Apply(CollectionsMarshal.AsSpan(_buffer));
                    }
                    finally
                    {
                        _buffer.Clear();
                    }
                    break;
            }
            return _current;
        }

        private static void Fill(List<IElement?> buffer, IEnumerable<IElement?> input)
        {
            // Indexing a Spread of another element type (covariant IReadOnlyList) needs no enumerator.
            if (input is IReadOnlyList<IElement?> list)
            {
                for (int i = 0; i < list.Count; i++)
                    buffer.Add(list[i]);
            }
            else
            {
                buffer.AddRange(input);
            }
        }

        private void Apply<T>(ReadOnlySpan<T> input) where T : IElement?
        {
            if (SameChildren(input, _current))
                return;
            _current = Build(input, _current);
            Built++;
        }

        private static bool SameChildren<T>(ReadOnlySpan<T> input, ImmutableArray<Element> current) where T : IElement?
        {
            int j = 0;
            foreach (var e in input)
            {
                if (!IsChild(e))
                    continue;
                if (j >= current.Length || !Matches(e!, current[j]))
                    return false;
                j++;
            }
            return j == current.Length;
        }

        private static bool IsChild(IElement? e) => e is Element or IStylable;

        private static bool Matches(IElement e, Element current)
            => ReferenceEquals(e, current) || current is StylableElement s && ReferenceEquals(e, s.Stylable);

        private static ImmutableArray<Element> Build<T>(ReadOnlySpan<T> input, ImmutableArray<Element> previous) where T : IElement?
        {
            int count = 0;
            foreach (var e in input)
                if (IsChild(e))
                    count++;
            if (count == 0)
                return ImmutableArray<Element>.Empty;

            var builder = ImmutableArray.CreateBuilder<Element>(count);
            foreach (var e in input)
            {
                if (e is Element element)
                    builder.Add(element);
                else if (e is IStylable stylable)
                    builder.Add(Find(previous, stylable) ?? new StylableElement(stylable));
            }
            return builder.MoveToImmutable();
        }

        private static StylableElement? Find(ImmutableArray<Element> previous, IStylable stylable)
        {
            foreach (var e in previous)
                if (e is StylableElement s && ReferenceEquals(s.Stylable, stylable))
                    return s;
            return null;
        }
    }
}
