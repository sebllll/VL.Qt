using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core.Import;
using VL.Model;
using VL.Lib.Mathematics;
using VL.Qt.Nodes;
using VL.Qt.NodeFactories;
using VL.Qt.Qml;

namespace VL.Qt.Tests
{
    /// <summary>The common pins of the generated QML nodes (<see cref="QmlCommonPins"/>) are those of QmlElement (QML-04).</summary>
    [TestFixture]
    public sealed class QmlCommonPinsTests
    {
        private static readonly MethodInfo s_update = typeof(QmlElementNode).GetMethod(nameof(QmlElementNode.Update))!;

        private static string PinName(ParameterInfo p) => PinReflection.PinName(p);

        private static bool IsVisible(ParameterInfo p) => PinReflection.IsVisible(p);

        private static object? DefaultOf(ParameterInfo p) => PinReflection.DefaultOf(p);

        [Test]
        public void Inputs_AreTheInputsOfQmlElement()
        {
            var parameters = s_update.GetParameters().Where(p => !p.IsOut).ToList();
            var expected = QmlCommonPins.LeadingInputs.Concat(new[] { QmlCommonPins.Properties }).Concat(QmlCommonPins.TrailingInputs).ToList();
            foreach (var pin in expected)
            {
                var p = parameters.Single(x => PinName(x) == pin.Name);
                Assert.That(p.ParameterType, Is.EqualTo(pin.Type), $"type of {pin.Name}");
                // The one documented difference: Properties is the main input of QmlElement, the plan B of a generated node (user-qml.md).
                if (pin != QmlCommonPins.Properties)
                    Assert.That(IsVisible(p), Is.EqualTo(pin.IsVisible), $"visibility of {pin.Name}");
                Assert.That(pin.Default, Is.EqualTo(DefaultOf(p)), $"default of {pin.Name}");
            }
        }

        [Test]
        public void Outputs_AreTheOutputsOfQmlElement()
        {
            var outputs = s_update.GetParameters().Where(p => p.IsOut).ToList();
            foreach (var pin in QmlCommonPins.TrailingOutputs)
            {
                var p = outputs.Single(x => PinName(x) == pin.Name);
                Assert.That(p.ParameterType.GetElementType(), Is.EqualTo(pin.Type), $"type of {pin.Name}");
                Assert.That(IsVisible(p), Is.EqualTo(pin.IsVisible), $"visibility of {pin.Name}");
            }
            Assert.That(s_update.ReturnParameter.GetCustomAttribute<PinAttribute>()!.Name, Is.EqualTo(QmlCommonPins.Output.Name));
            Assert.That(outputs.Select(PinName), Does.Not.Contain("Error"));
        }

        [Test]
        public void Order_FollowsQmlElement()
        {
            var names = s_update.GetParameters().Where(p => !p.IsOut).Select(PinName).ToList();
            var common = QmlCommonPins.LeadingInputs.Select(p => p.Name).Concat(new[] { "File", "Properties" }).Concat(QmlCommonPins.TrailingInputs.Select(p => p.Name));
            Assert.That(names, Is.EqualTo(common));
        }

        [Test]
        public void ReservedNames_AreTheCommonPinsAndProperties()
        {
            Assert.That(QmlCommonPins.ReservedNames, Is.EquivalentTo(new[]
            {
                "Properties", "Auto Size", "Size", "Transform", "Anchor", "Opacity", "Clip", "Debug", "Interaction", "Effect", "Enabled",
                "Output", "Measured Size", "Scene Bounds",
            }));
        }
    }
}
