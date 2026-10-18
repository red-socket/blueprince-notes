using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BluePrinceNotes.Notes;

/// <summary>
/// All notes, kept in one JSON file (UserData/BluePrinceNotes/notes.json).
///
/// Writes go to notes.json.tmp first, then replace notes.json, so a crash mid-write leaves the old file whole.
/// The first write of a session copies the old file to notes.json.bak. A file that can't be read is never
/// overwritten: it is copied to notes.json.corrupt-(time) and the backup is loaded instead; if even the copy
/// fails, or the file comes from a newer version of the mod, saving stays off for the session.
/// Saves happen a moment after the last change, and at least every few seconds while changes keep coming.
/// Blank notes are never written.
/// </summary>
internal sealed class NoteStore
{
    private const int FormatVersion = 1;
    private const double SaveDelay = 1.5;   // seconds after the last change
    private const double MaxDelay = 5;      // at least this often while changes keep coming
    private const double RetryDelay = 5;
    private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // accents stay readable in the file
        PropertyNameCaseInsensitive = true,                     // a hand-edited file may use other cases
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,  // a NaN must not block saving
    };

    private sealed class VersionProbe
    {
        public int Version { get; set; }
    }

    private sealed class NoteFile
    {
        public int Version { get; set; }
        public List<Note> Notes { get; set; } = new();
    }

    private readonly string _path, _tmp, _bak;
    private readonly List<Note> _notes = new();
    private readonly Action<string> _log, _warn;
    private bool _backedUp, _warnedBackup;
    private double _saveAt = -1, _saveBy = -1;

    public NoteStore(string directory, Action<string> log, Action<string> warn)
    {
        Folder = directory;
        _path = Path.Combine(directory, "notes.json");
        _tmp = _path + ".tmp";
        _bak = _path + ".bak";
        _log = log;
        _warn = warn;
    }

    public string Folder { get; }
    public string FilePath => _path;
    public IReadOnlyList<Note> Notes => _notes;
    public bool Dirty { get; private set; }
    /// <summary>Saving is off for this session (a file we couldn't read or set aside, or from a newer version).</summary>
    public bool ReadOnly { get; private set; }
    /// <summary>The last save attempt failed (it is retried).</summary>
    public bool SaveFailing { get; private set; }
    public bool NotSaving => ReadOnly || SaveFailing;

    public void Load()
    {
        _notes.Clear();
        Dirty = false;
        if (!File.Exists(_path))
        {
            // No notes.json but a backup (the file was deleted or moved by hand): read the backup.
            if (File.Exists(_bak) && TryRead(_bak, out var fromBak, out _))
            {
                _notes.AddRange(fromBak);
                _warn($"notes.json is missing; loaded {_notes.Count} note(s) from notes.json.bak");
            }
            return;
        }
        if (TryRead(_path, out var list, out var error))
        {
            _notes.AddRange(list);
            _log($"loaded {_notes.Count} note(s) from {_path}");
            return;
        }

        // Never overwrite a file we can't read: keep a copy, then fall back to the backup.
        _warn($"can't read {_path}: {error}");
        var aside = $"{_path}.corrupt-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        try
        {
            File.Copy(_path, aside, overwrite: false);
            _warn($"kept a copy as {Path.GetFileName(aside)}");
        }
        catch (Exception e)
        {
            ReadOnly = true;
            _warn($"couldn't keep a copy ({e.Message}); notes won't be saved this session");
            return;
        }
        _backedUp = true;  // the old file is not worth backing up over notes.json.bak
        if (File.Exists(_bak) && TryRead(_bak, out var older, out _))
        {
            _notes.AddRange(older);
            _warn($"loaded {_notes.Count} note(s) from notes.json.bak");
        }
    }

    private bool TryRead(string path, out List<Note> notes, out string error)
    {
        notes = null;
        error = null;
        try
        {
            ReadOnlySpan<byte> data = File.ReadAllBytes(path);
            if (data.StartsWith(Bom)) data = data.Slice(3);  // saved by a text editor
            // The version first: a newer file must never go down the corrupt path, even if this version can't parse it.
            var probe = JsonSerializer.Deserialize<VersionProbe>(data, Json);
            if (probe == null) { error = "empty file"; return false; }
            NoteFile file;
            if (probe.Version > FormatVersion)
            {
                // Saving would drop what the newer version stored.
                ReadOnly = true;
                _warn($"{Path.GetFileName(path)} was written by a newer version of the mod; notes won't be saved this session");
                try { file = JsonSerializer.Deserialize<NoteFile>(data, Json) ?? new NoteFile(); }
                catch (Exception e)
                {
                    _warn($"can't show its notes: {e.Message}");
                    file = new NoteFile();
                }
            }
            else
            {
                file = JsonSerializer.Deserialize<NoteFile>(data, Json);
                if (file == null) { error = "empty file"; return false; }
            }
            notes = new List<Note>();
            foreach (var n in file.Notes ?? new List<Note>())
            {
                if (n == null || string.IsNullOrWhiteSpace(n.Text)) continue;
                n.Id ??= Guid.NewGuid().ToString("N");
                n.Context ??= new NoteContext();
                n.Context.Date ??= "";
                n.Context.Area ??= "";
                n.Context.Room ??= "";
                notes.Add(n);
            }
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    public void Add(Note n, double now)
    {
        _notes.Add(n);
        Changed(now);
    }

    public void Remove(Note n, double now)
    {
        if (_notes.Remove(n)) Changed(now);
    }

    /// <summary>A note changed: save a moment after the last change.</summary>
    public void Changed(double now)
    {
        if (_saveBy < 0) _saveBy = now + MaxDelay;
        Dirty = true;
        _saveAt = Math.Min(now + SaveDelay, _saveBy);
    }

    /// <summary>Call every frame; saves when a change has settled.</summary>
    public void Tick(double now)
    {
        if (Dirty && _saveAt >= 0 && now >= _saveAt) Save(now);
    }

    /// <summary>Saves now if anything changed. False if it couldn't (it retries a few seconds later).</summary>
    public bool Flush(double now) => !Dirty || Save(now);

    private bool Save(double now)
    {
        _saveBy = -1;
        if (ReadOnly)
        {
            _saveAt = -1;
            return false;
        }
        try
        {
            Directory.CreateDirectory(Folder);
            if (!_backedUp && File.Exists(_path))
            {
                // A failed backup must not stop the save itself; it is tried again until a save goes through.
                try
                {
                    File.Copy(_path, _bak, overwrite: true);
                    _backedUp = true;
                }
                catch (Exception e)
                {
                    if (!_warnedBackup) _warn($"couldn't update notes.json.bak ({e.Message}); saving anyway");
                    _warnedBackup = true;
                }
            }

            var file = new NoteFile { Version = FormatVersion, Notes = _notes.FindAll(n => !n.IsBlank) };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(file, Json);
            using (var fs = new FileStream(_tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(_tmp, _path, overwrite: true);
            _backedUp = true;  // the session's original file is replaced now, backed up or not
            Dirty = false;
            SaveFailing = false;
            _saveAt = -1;
            return true;
        }
        catch (Exception e)
        {
            SaveFailing = true;
            _warn($"saving notes failed, retrying in {RetryDelay:0} s: {e.Message}");
            _saveAt = now + RetryDelay;
            return false;
        }
    }
}
