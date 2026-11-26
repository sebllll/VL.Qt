using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Tests
{
    /// <summary>LoadFont against the fake font API: order of add and release, errors, Dispose.</summary>
    public class LoadFontNodeTests
    {
        private FakeFontApi _api = null!;
        private RecordingMessages _messages = null!;

        [SetUp]
        public void SetUp()
        {
            _api = new FakeFontApi();
            _messages = new RecordingMessages();
        }

        private LoadFontNode Node() => new(_api, _messages.Errors);

        private static Path P(string path) => new(path);

        [Test]
        public void Load_ReturnsFirstFamily_AndRegistersOnce()
        {
            _api.Families = "First" + (char)10 + "Second";
            using var node = Node();
            Assert.That(node.Update(P("a.ttf")), Is.EqualTo("First"));
            Assert.That(node.Update(P("a.ttf")), Is.EqualTo("First"));
            Assert.That(_api.Calls, Is.EqualTo(new[] { "add a.ttf" }), "unchanged path = no second call");
            Assert.That(_messages.Shown, Is.Null);
        }

        [Test]
        public void PathChange_AddsBeforeReleasingThePrevious()
        {
            using var node = Node();
            node.Update(P("a.ttf"));
            Assert.That(node.Update(P("b.ttf")), Is.EqualTo("Fake Sans"));
            Assert.That(_api.Calls, Is.EqualTo(new[] { "add a.ttf", "add b.ttf", "release 1" }));
        }

        [Test]
        public void EmptyPath_ReleasesAndReturnsNoFamily()
        {
            using var node = Node();
            node.Update(P("a.ttf"));
            Assert.That(node.Update(P("")), Is.Empty);
            Assert.That(_api.Calls, Is.EqualTo(new[] { "add a.ttf", "release 1" }));
        }

        [Test]
        public void AddFails_ShowsError_ReleasesPrevious_ClearsFamily()
        {
            using var node = Node();
            node.Update(P("a.ttf"));
            _api.AddRc = Native.Result.Io;
            Assert.That(node.Update(P("bad.ttf")), Is.Empty);
            Assert.That(_messages.Shown, Is.EqualTo("font failure"));
            Assert.That(_api.Calls, Is.EqualTo(new[] { "add a.ttf", "add bad.ttf", "release 1" }));
            _api.AddRc = Native.Result.Ok;
            Assert.That(node.Update(P("b.ttf")), Is.EqualTo("Fake Sans"));
            Assert.That(_messages.Shown, Is.Null, "a new path clears the error");
        }

        [Test]
        public void WrongThread_ShowsError_WithoutNativeCall()
        {
            _api.OnQtThread = false;
            using var node = Node();
            Assert.That(node.Update(P("a.ttf")), Is.Empty);
            Assert.That(_messages.Shown, Does.Contain("thread of the Qt app"));
            Assert.That(_api.Calls, Is.Empty);
        }

        [Test]
        public void Dispose_ReleasesTheHandleOnce()
        {
            var node = Node();
            node.Update(P("a.ttf"));
            node.Dispose();
            Assert.That(_api.Calls, Is.EqualTo(new[] { "add a.ttf", "release 1" }));
        }

        [Test]
        public void Dispose_WithoutFont_CallsNothing()
        {
            var node = Node();
            node.Update(P(""));
            node.Dispose();
            Assert.That(_api.Calls, Is.Empty);
        }

        [Test]
        public void ReleaseFails_IsLoggedOnly()
        {
            using var node = Node();
            node.Update(P("a.ttf"));
            _api.ReleaseRc = Native.Result.UnknownId;
            node.Update(P("b.ttf"));
            Assert.That(_messages.Logged, Does.Contain("font failure"));
            Assert.That(_messages.Shown, Is.Null);
        }
    }
}
