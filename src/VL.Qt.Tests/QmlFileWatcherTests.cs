using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Text;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;
using Path = VL.Lib.IO.Path;
using P = VL.Qt.Wire.Protocol.PropertyId;

namespace VL.Qt.Tests
{
    /// <summary>QmlFileWatcher: which changes count, folder only, change flag taken once.</summary>
    public class QmlFileWatcherTests
    {
        private string _dir = null!;
        private readonly Subject<FileSystemEventArgs> _events = new();
        private int _subscriptions;
        private string? _watched;

        [SetUp]
        public void SetUp()
        {
            _dir = Directory.CreateTempSubdirectory("vlqt-qmlwatch").FullName;
            _subscriptions = 0;
            _watched = null;
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_dir, true);

        private QmlFileWatcher Watcher() => new(dir =>
        {
            _watched = dir;
            return System.Reactive.Linq.Observable.Create<FileSystemEventArgs>(o =>
            {
                _subscriptions++;
                var d = _events.Subscribe(o);
                return () => { _subscriptions--; d.Dispose(); };
            });
        });

        private FileSystemEventArgs Changed(string name) => new(WatcherChangeTypes.Changed, _dir, name);

        [TestCase("a.qml", true)]
        [TestCase("Other.QML", true)]
        [TestCase("qmldir", true)]
        [TestCase("QMLDIR", true)]
        [TestCase("sub/qmldir", true)]
        [TestCase("a.js", false)]
        [TestCase("a.qml.bak", false)]
        [TestCase("a.qmlc", false)]
        [TestCase("notes.txt", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void IsRelevantName(string? name, bool expected) => Assert.That(QmlFileWatcher.IsRelevantName(name), Is.EqualTo(expected));

        [Test]
        public void Rename_CountsForTheOldOrTheNewName()
        {
            Assert.That(QmlFileWatcher.IsRelevant(new RenamedEventArgs(WatcherChangeTypes.Renamed, _dir, "a.qml~", "a.qml")), Is.True, "temp file renamed to a.qml");
            Assert.That(QmlFileWatcher.IsRelevant(new RenamedEventArgs(WatcherChangeTypes.Renamed, _dir, "a.bak", "a.qml")), Is.True, "a.qml renamed away");
            Assert.That(QmlFileWatcher.IsRelevant(new RenamedEventArgs(WatcherChangeTypes.Renamed, _dir, "a.txt", "b.txt")), Is.False);
        }

        [Test]
        public void WatchesTheFolderOfTheFile_ChangeIsTakenOnce()
        {
            using var w = Watcher();
            w.Update(System.IO.Path.Combine(_dir, "a.qml"));
            Assert.That(_watched, Is.EqualTo(_dir));
            Assert.That(w.IsWatching, Is.True);
            Assert.That(w.TakeChanged(), Is.False);
            _events.OnNext(Changed("Other.qml")); // a component next to the file counts
            Assert.That(w.TakeChanged(), Is.True);
            Assert.That(w.TakeChanged(), Is.False);
            _events.OnNext(Changed("readme.txt"));
            _events.OnNext(Changed("helper.js")); // .js files are not watched
            Assert.That(w.TakeChanged(), Is.False);
            _events.OnNext(Changed("qmldir"));
            Assert.That(w.TakeChanged(), Is.True);
        }

        [Test]
        public void SamePath_DoesNotResubscribe_NewPathForgetsTheChange()
        {
            using var w = Watcher();
            var a = System.IO.Path.Combine(_dir, "a.qml");
            w.Update(a);
            w.Update(a);
            Assert.That(_subscriptions, Is.EqualTo(1));
            _events.OnNext(Changed("a.qml"));
            w.Update(System.IO.Path.Combine(_dir, "b.qml"));
            Assert.That(w.TakeChanged(), Is.False, "a change reported for the old path is forgotten");
            Assert.That(_subscriptions, Is.EqualTo(1), "the old subscription is gone");
        }

        [Test]
        public void EmptyPath_Stops()
        {
            using var w = Watcher();
            w.Update(System.IO.Path.Combine(_dir, "a.qml"));
            w.Update("");
            Assert.That(w.IsWatching, Is.False);
            Assert.That(_subscriptions, Is.EqualTo(0));
        }

        [Test]
        public void MissingFolder_IsNotWatched_UntilThePathIsSetAgain()
        {
            using var w = Watcher();
            var missing = System.IO.Path.Combine(_dir, "later", "a.qml");
            w.Update(missing);
            Assert.That(w.IsWatching, Is.False);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(missing)!);
            w.Update(missing); // same path: nothing happens
            Assert.That(w.IsWatching, Is.False);
            w.Update("");
            w.Update(missing);
            Assert.That(w.IsWatching, Is.True);
        }

        [Test]
        public void Dispose_EndsTheSubscription()
        {
            var w = Watcher();
            w.Update(System.IO.Path.Combine(_dir, "a.qml"));
            w.Dispose();
            Assert.That(_subscriptions, Is.EqualTo(0));
            _events.OnNext(Changed("a.qml"));
            Assert.That(w.TakeChanged(), Is.False);
        }
    }
}
