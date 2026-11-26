using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using VL.Core;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Nodes;

namespace VL.Qt.Audio.Tests
{
    /// <summary>
    /// Video (VL.Audio) promises the same pins as Video and copies them by hand: a pin added to or renamed in VideoNode
    /// alone must fail here.
    /// </summary>
    [TestFixture]
    public sealed class VideoAudioNodePinTests
    {
        // The only pins the audio variant has on top of Video.
        private static readonly string[] s_extraPins = { "audioOffset", "audio", "syncError" };

        // Differences allowed besides the extra pins, as "<pin>: <what>". Empty today. The Decided lines of C2/O3 give both
        // nodes `Enabled` and `FillMode` later; if that lands in one node first, list the difference here and let the
        // issue of C2/O3 delete the list.
        private static readonly string[] s_allowedDifferences = Array.Empty<string>();

        private sealed record PinInfo(string Name, Type Type, object? Default, Type? DefaultType, PinVisibility Visibility)
        {
            public override string ToString() => $"{Name}: {Type.Name} default={Default ?? "none"} {Visibility}";
        }

        private static List<PinInfo> PinsOf(Type node)
        {
            var update = node.GetMethod("Update", BindingFlags.Public | BindingFlags.Instance)!;
            return update.GetParameters().Select(p =>
            {
                var pin = p.GetCustomAttribute<PinAttribute>();
                var def = p.GetCustomAttribute<DefaultValueAttribute>();
                var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
                return new PinInfo(p.Name!, type, def?.Value, def is null ? null : DefaultTypeOf(def),
                    pin?.Visibility ?? PinVisibility.Visible);
            }).ToList();
        }

        // DefaultValue(typeof(T), "text") keeps the value as string; its type is part of the pin.
        private static Type? DefaultTypeOf(DefaultValueAttribute a) => a.Value?.GetType();

        [Test]
        public void VideoPinsAppearInVideoAudioInTheSameOrder()
        {
            var video = PinsOf(typeof(VideoNode));
            var audio = PinsOf(typeof(VideoAudioNode));
            var audioWithoutExtras = audio.Where(p => !s_extraPins.Contains(p.Name)).ToList();

            Assert.That(audio.Select(p => p.Name).Where(s_extraPins.Contains).OrderBy(n => n),
                Is.EqualTo(s_extraPins.OrderBy(n => n)), "the audio variant must have exactly the extra pins");

            var allowed = new HashSet<string>(s_allowedDifferences.Select(d => d.Split(':')[0]));
            video = video.Where(p => !allowed.Contains(p.Name)).ToList();
            audioWithoutExtras = audioWithoutExtras.Where(p => !allowed.Contains(p.Name)).ToList();

            Assert.That(audioWithoutExtras.Select(p => p.ToString()), Is.EqualTo(video.Select(p => p.ToString())),
                "pins of Video (name, type, default, visibility, order) and Video (VL.Audio) differ");
        }
    }
}
