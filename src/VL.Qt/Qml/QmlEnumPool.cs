using System;
using System.Collections.Generic;
using System.Text;

namespace VL.Qt.Qml
{
    /// <summary>
    /// Pool of precompiled dynamic enum types (QmlEnum01 … QmlEnum32, <see cref="QmlEnumSlotTypes"/>) for the dropdown pins of QML
    /// enums. vvvv's compiler loads only the assemblies a document references, so a type made at run time cannot be the type of a
    /// pin; a pool type of VL.Qt.dll can. A patch saves the type of a pin (an IOBox holds the name of the entry, its type QmlEnum07),
    /// so an enum keeps its type over restarts, compilations and exports.
    /// <list type="bullet">
    /// <item>Key: <c>Type.Enum</c> of the declaration ("Gauge.Mode"), unique in a qml folder because file names are.</item>
    /// <item>
    /// Mapping: the file <see cref="QmlEnumSlotFile.FileName"/> in the qml folder (<see cref="QmlEnumSlotFile.RootOf"/>), one line
    /// per key. It lives with the QML files, so it goes along into version control, into a package and into an export (the build
    /// copies the whole qml folder). A new key gets the first free type from a place given by a hash of the key, so that folders
    /// written apart rarely pick the same type, and the file is written at once.
    /// </item>
    /// <item>
    /// Read-only folder: the qml folder of an installed package (<see cref="QmlEnumSlotFile.IsInstalledPackage"/>) is never
    /// written, nor is a folder whose file cannot be read or written. Its keys get the types of its file; a key without a line
    /// gets no type, as a type assigned for one session only would not match the saved patches the next time. The package
    /// author ships the file, which VL.Qt wrote in the source folder.
    /// </item>
    /// <item>
    /// Collision: the lines of a file are reserved when the folder is first used. When two folders reserve the same type, a
    /// read-only file wins over a writable one (it cannot change); between equals the first one wins. The writable loser gets a
    /// new type, written to its file, and a message once. If the winner's type was already handed out in this session, it stays
    /// until vvvv restarts: the read-only loser gets no type now, and the writable winner moves in its file for the next start.
    /// Two read-only files on the same type: the second gets no type and a message to change one of the files.
    /// </item>
    /// <item>Full: when all types are reserved, a new key gets no type; deleting lines of enums that no longer exist frees them.</item>
    /// </list>
    /// Without a type (<see cref="QmlEnumClaim.Slot"/> null) the caller keeps the int pin. Thread-safe.
    /// </summary>
    internal sealed class QmlEnumPool
    {
        /// <summary>
        /// Number of pool types. To change it, change this constant, build, run the tests with VLQT_UPDATE_SNAPSHOT=1 (it rewrites
        /// QmlEnumSlotTypes.cs, see QmlEnumPoolTests), then build and run them again (node-API snapshot).
        /// </summary>
        public const int SlotCount = 32;

        /// <summary>The pool of the pins; the definitions of QmlEnum01 … read their entries here.</summary>
        public static QmlEnumPool Shared { get; } = new(QmlEnumSlotTypes.All);

        private readonly object _syncRoot = new();
        private readonly QmlEnumSlot[] _slots;
        private readonly Owner?[] _owners;
        private readonly Dictionary<string, Folder> _folders = new(QmlEnumSlotFile.PathComparer);

        /// <summary>A pool of <paramref name="types"/> (the tests use fewer than <see cref="SlotCount"/>).</summary>
        internal QmlEnumPool(IReadOnlyList<QmlEnumSlotType> types)
        {
            _slots = new QmlEnumSlot[types.Count];
            for (var i = 0; i < types.Count; i++)
                _slots[i] = new QmlEnumSlot(i + 1, types[i]);
            _owners = new Owner?[types.Count];
        }

        /// <summary>The key of an enum in the mapping file: "Gauge.Mode".</summary>
        public static string KeyOf(QmlEnumDecl decl) => decl.Type + "." + decl.Name;

        /// <summary>
        /// The pool type of <paramref name="decl"/>, used by the node of the QML file <paramref name="qmlFilePath"/> (the qml folder
        /// of the file holds the mapping). Call it again after the enum changed (hot reload): same type, the entries follow and the
        /// dropdowns update. No slot: keep the int pin and show the message.
        /// </summary>
        public QmlEnumClaim Claim(string qmlFilePath, QmlEnumDecl decl)
        {
            var key = KeyOf(decl);
            QmlEnumSlot slot;
            string? message;
            lock (_syncRoot)
            {
                var root = QmlEnumSlotFile.RootOf(qmlFilePath);
                if (!_folders.TryGetValue(root, out var folder))
                    folder = Load(root);
                var index = ClaimIn(folder, key, out message);
                if (index < 0)
                    return new QmlEnumClaim(null, message);
                slot = _slots[index];
            }
            slot.SetEntries(decl);
            return new QmlEnumClaim(slot, message);
        }

        /// <summary>Entries of slot <paramref name="number"/>, for the definition of its type.</summary>
        internal IReadOnlyDictionary<string, object> Entries(int number) => _slots[number - 1].Entries;

        /// <summary>Fires when the entries of slot <paramref name="number"/> change, for the definition of its type.</summary>
        internal IObservable<object> EntriesChanged(int number) => _slots[number - 1].Changed;

        // Index of the slot of key, or -1 with the reason in message.
        private int ClaimIn(Folder folder, string key, out string? message)
        {
            message = null;
            if (folder.Claimed.TryGetValue(key, out var claimed))
                return claimed;
            if (folder.Blocked.TryGetValue(key, out message))
                return -1;
            if (folder.Map.TryGetValue(key, out var number) && _owners[number - 1] is { } owner && owner.Folder == folder && owner.Key == key)
                return Take(number - 1, owner);

            if (folder.ReadOnlyReason is { } reason)
            {
                message = $"{key} has no line in {folder.FilePath}, and {reason}: its pins stay numbers.";
                return -1;
            }
            var free = FindFree(key);
            if (free < 0)
            {
                message = $"All {_slots.Length} pool types for QML enums are taken (see {QmlEnumSlotFile.FileName} in the qml folders): " +
                    $"the pins of {key} stay numbers. Delete the lines of enums that no longer exist.";
                return -1;
            }
            var map = new Dictionary<string, int>(folder.Map) { [key] = free + 1 };
            if (!QmlEnumSlotFile.TryWrite(folder.FilePath, map))
            {
                folder.ReadOnlyReason = "the file cannot be written";
                message = $"{folder.FilePath} cannot be written: the pins of {key} stay numbers.";
                return -1;
            }
            folder.Map[key] = free + 1;
            folder.Moved.Remove(key, out message);
            return Take(free, Reserve(free, folder, key));
        }

        private static int Take(int index, Owner owner)
        {
            owner.Claimed = true;
            owner.Folder.Claimed[owner.Key] = index;
            return index;
        }

        private Owner Reserve(int index, Folder folder, string key) => _owners[index] = new Owner(folder, key);

        // Reads the file of a folder used for the first time and reserves its lines (collisions: see the head comment).
        private Folder Load(string root)
        {
            var folder = new Folder(root);
            _folders.Add(root, folder);
            var lines = QmlEnumSlotFile.Read(folder.FilePath, _slots.Length);
            if (lines is null)
                folder.ReadOnlyReason = "the file cannot be read";
            else if (QmlEnumSlotFile.IsInstalledPackage(root))
                folder.ReadOnlyReason = "the folder belongs to an installed package (its author ships the file)";

            foreach (var (key, number) in lines ?? new List<(string, int)>())
            {
                var index = number - 1;
                folder.Map[key] = number;
                if (_owners[index] is not { } other)
                {
                    Reserve(index, folder, key);
                    continue;
                }
                var type = TypeName(index);
                if (folder.ReadOnlyReason is null)
                {
                    // The writable newcomer moves: without its line it gets a new type at its claim, which rewrites the file.
                    folder.Map.Remove(key);
                    folder.Moved[key] = $"{key} moved from {type} to another type, because {other.Key} of {other.Folder.FilePath} has {type}: " +
                        $"recreate the IOBoxes saved with {type}.";
                }
                else if (other.Folder.ReadOnlyReason is null && !other.Claimed)
                {
                    // The read-only newcomer wins over a writable reservation that is not handed out yet.
                    other.Folder.Map.Remove(other.Key);
                    other.Folder.Moved[other.Key] = $"{other.Key} moved from {type} to another type, because {key} of {folder.FilePath} " +
                        $"has {type}: recreate the IOBoxes saved with {type}.";
                    Reserve(index, folder, key);
                }
                else if (other.Folder.ReadOnlyReason is null)
                {
                    folder.Blocked[key] = $"{key} has {type} in {folder.FilePath}, which {other.Key} of {other.Folder.FilePath} uses in this " +
                        $"session: the pins of {key} stay numbers until vvvv restarts.";
                    MoveOnDisk(other);
                }
                else
                {
                    folder.Blocked[key] = $"{key} of {folder.FilePath} and {other.Key} of {other.Folder.FilePath} both have {type}: the pins of " +
                        $"{key} stay numbers. Change the number in one of the files.";
                }
            }
            return folder;
        }

        // A writable owner whose type is handed out keeps it until the restart, but its file names a free type for the next start.
        private void MoveOnDisk(Owner owner)
        {
            var free = FindFree(owner.Key);
            if (free < 0)
                return;
            var map = new Dictionary<string, int>(owner.Folder.Map) { [owner.Key] = free + 1 };
            if (!QmlEnumSlotFile.TryWrite(owner.Folder.FilePath, map))
                return;
            owner.Folder.Map[owner.Key] = free + 1;
            // Reserved for the next start; the owner's claims in this session still get the old type (Folder.Claimed).
            Reserve(free, owner.Folder, owner.Key);
        }

        // First free slot from a place given by the key; -1 if all are reserved.
        private int FindFree(string key)
        {
            var start = (int)(Fnv1a(key) % (uint)_slots.Length);
            for (var i = 0; i < _slots.Length; i++)
            {
                var index = (start + i) % _slots.Length;
                if (_owners[index] is null)
                    return index;
            }
            return -1;
        }

        // Stable over processes and machines, unlike string.GetHashCode.
        private static uint Fnv1a(string text)
        {
            var hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(text))
                hash = (hash ^ b) * 16777619u;
            return hash;
        }

        private string TypeName(int index) => _slots[index].Type.Name;

        /// <summary>A qml folder: its mapping file and what this session did with it.</summary>
        private sealed class Folder
        {
            public Folder(string root)
            {
                FilePath = System.IO.Path.Combine(root, QmlEnumSlotFile.FileName);
            }

            public string FilePath { get; }

            /// <summary>Why the file is never written (shown with a key without a line), null if it is writable.</summary>
            public string? ReadOnlyReason { get; set; }

            /// <summary>Key → slot number as the file has it (after collisions).</summary>
            public Dictionary<string, int> Map { get; } = new(StringComparer.Ordinal);

            /// <summary>Key → slot index handed out in this session: it stays until the restart.</summary>
            public Dictionary<string, int> Claimed { get; } = new(StringComparer.Ordinal);

            /// <summary>Key → message of a move, given with the next claim.</summary>
            public Dictionary<string, string> Moved { get; } = new(StringComparer.Ordinal);

            /// <summary>Key → why it gets no type in this session.</summary>
            public Dictionary<string, string> Blocked { get; } = new(StringComparer.Ordinal);
        }

        /// <summary>The reservation of a slot by a key of a folder; Claimed once its type is handed out.</summary>
        private sealed class Owner
        {
            public Owner(Folder folder, string key)
            {
                Folder = folder;
                Key = key;
            }

            public Folder Folder { get; }

            public string Key { get; }

            public bool Claimed { get; set; }
        }
    }
}
