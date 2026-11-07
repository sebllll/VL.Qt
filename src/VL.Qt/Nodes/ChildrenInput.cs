using System;
using System.Collections.Immutable;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Styles;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Turns the children pin group (a <see cref="Spread{T}"/> that VL may rebuild every frame) into a stable
    /// <see cref="ImmutableArray{T}"/>: unconnected pins (null) and unknown <see cref="IElement"/> implementations are
    /// dropped, an <see cref="IStylable"/> becomes a <see cref="StylableElement"/> (#413, the instance is kept while the
    /// stylable stays the same), and the same array instance is returned as long as the children are reference equal
    /// element by element. Since <see cref="ImmutableArray{T}"/> equality is reference equality of its backing array, the
    /// result can go straight into a node's <c>Props</c> record struct for change detection. No allocation if nothing
    /// changed. One instance per node, main thread only.
    /// </summary>
    internal sealed class ChildrenInput
    {
        Spread<IElement?>? _lastInput;
        ImmutableArray<Element> _current = ImmutableArray<Element>.Empty;

        /// <summary>Current children (never default).</summary>
        public ImmutableArray<Element> Current => _current;

        /// <summary>Number of arrays built so far (for tests).</summary>
        public int Built { get; private set; }

        public ImmutableArray<Element> Update(Spread<IElement?>? input)
        {
            if (ReferenceEquals(input, _lastInput))
                return _current;
            _lastInput = input;

            var span = input is null ? ReadOnlySpan<IElement?>.Empty : input.AsSpan();
            if (!SameChildren(span, _current))
            {
                _current = Build(span, _current);
                Built++;
            }
            return _current;
        }

        static bool SameChildren(ReadOnlySpan<IElement?> input, ImmutableArray<Element> current)
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

        static bool IsChild(IElement? e) => e is Element or IStylable;

        static bool Matches(IElement e, Element current)
            => ReferenceEquals(e, current) || current is StylableElement s && ReferenceEquals(e, s.Stylable);

        static ImmutableArray<Element> Build(ReadOnlySpan<IElement?> input, ImmutableArray<Element> previous)
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

        static StylableElement? Find(ImmutableArray<Element> previous, IStylable stylable)
        {
            foreach (var e in previous)
                if (e is StylableElement s && ReferenceEquals(s.Stylable, stylable))
                    return s;
            return null;
        }
    }
}
