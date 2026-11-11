// Shared source: this file is compiled into the service (net10.0) AND linked into the installer's custom-action
// assembly (net48, setup/WinSmtpRelay.Setup.CustomActions). Keep it free of APIs that .NET Framework 4.8 lacks
// (no Path.GetRelativePath, no File.Move overwrite overload, no newer string APIs).
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WinSmtpRelay.DataLocation
{
    /// <summary>
    /// Moves the writable data of a pre-ProgramData install (the SQLite database and its WAL/SHM side files, the
    /// Data Protection key ring, the self-signed admin certificate, the appsettings files) out of the program
    /// folder into the data directory (<c>%ProgramData%\Ardimedia\WinSmtpRelay</c>) — exactly once.
    /// <list type="bullet">
    /// <item>Idempotent: a <c>.migrated</c> marker in the data directory ends every later run immediately.</item>
    /// <item>Never overwrites: when a file already exists in the data directory the data directory wins; the
    /// legacy copy stays where it is and is reported. A database is moved as one group (db + -wal + -shm +
    /// -journal) or not at all, so a WAL file can never be paired with a different database.</item>
    /// <item>Reversible: every move is written to a journal before it happens. <see cref="Rollback"/> moves
    /// everything back (used by the installer's rollback action and when the service-side move fails);
    /// <see cref="Commit"/> drops the journal once the surrounding installation succeeded.</item>
    /// <item>Every step is reported through the log callback.</item>
    /// </list>
    /// </summary>
    public sealed class LegacyDataMigrator
    {
        /// <summary>Marker file in the data directory: the migration ran (its content lists what moved).</summary>
        public const string MarkerFileName = ".migrated";

        /// <summary>Journal of the moves of the current, not yet committed run (source TAB target per line).</summary>
        public const string JournalFileName = ".migration-journal";

        /// <summary>Name of the Data Protection key-ring folder (same name in the old and the new location).</summary>
        public const string KeysFolderName = "keys";

        private static readonly string[] DatabaseSideFileSuffixes = { "-wal", "-shm", "-journal" };

        private static readonly string[] SingleFileNames = { "admin-cert.pfx", "initial-admin-password.txt" };

        private readonly Action<string> log;

        public LegacyDataMigrator(Action<string> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>True when the data directory carries the marker of a completed migration.</summary>
        public static bool IsMigrated(string dataDirectory) =>
            File.Exists(Path.Combine(dataDirectory, MarkerFileName));

        /// <summary>
        /// Moves the legacy data from <paramref name="legacyDirectory"/> into <paramref name="dataDirectory"/>.
        /// Throws when a move fails; the caller then runs <see cref="Rollback"/> (the installer does it through its
        /// rollback custom action).
        /// </summary>
        public MigrationResult Migrate(string legacyDirectory, string dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new ArgumentException("The data directory is required.", nameof(dataDirectory));

            var result = new MigrationResult();
            if (IsMigrated(dataDirectory))
            {
                log("Data migration: already done (marker " + Path.Combine(dataDirectory, MarkerFileName) + ") - nothing to do.");
                result.AlreadyMigrated = true;
                return result;
            }

            if (!string.IsNullOrWhiteSpace(legacyDirectory) && SamePath(legacyDirectory, dataDirectory))
            {
                log("Data migration: the program folder IS the data directory (" + dataDirectory + ") - nothing to move, no marker written.");
                result.SameDirectory = true;
                return result;
            }

            Directory.CreateDirectory(dataDirectory);
            var journalPath = Path.Combine(dataDirectory, JournalFileName);
            // The journal exists from the first step on: a rollback of this run also removes the marker it writes,
            // even when nothing had to be moved (a fresh install).
            File.AppendAllText(journalPath, "# migration started " + DateTime.UtcNow.ToString("o") + Environment.NewLine, Encoding.UTF8);

            if (string.IsNullOrWhiteSpace(legacyDirectory) || !Directory.Exists(legacyDirectory))
            {
                log("Data migration: no legacy folder (" + (legacyDirectory ?? "(none)") + ") - nothing to move.");
            }
            else
            {
                log("Data migration: moving legacy data from " + legacyDirectory + " to " + dataDirectory + ".");
                MoveDatabases(legacyDirectory, dataDirectory, journalPath, result);
                MoveSingleFiles(legacyDirectory, dataDirectory, journalPath, result);
                MoveKeyRing(legacyDirectory, dataDirectory, journalPath, result);
            }

            WriteMarker(legacyDirectory, dataDirectory, result);
            log("Data migration: done - " + result.Moved.Count + " file(s) moved, " + result.Skipped.Count + " kept in place.");
            return result;
        }

        /// <summary>Forgets the journal: the run that moved the files is final.</summary>
        public void Commit(string dataDirectory)
        {
            var journalPath = Path.Combine(dataDirectory, JournalFileName);
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
                log("Data migration: committed (journal removed).");
            }
        }

        /// <summary>
        /// Moves every journalled file back to its legacy location and removes the marker, restoring the state before
        /// <see cref="Migrate"/>. Best effort: a file that cannot be moved back is logged and left in the data
        /// directory, never deleted.
        /// </summary>
        public void Rollback(string dataDirectory)
        {
            var journalPath = Path.Combine(dataDirectory, JournalFileName);
            if (!File.Exists(journalPath))
            {
                log("Data migration rollback: no journal - nothing to undo.");
                return;
            }

            var moves = File.ReadAllLines(journalPath)
                .Where(l => l.Length > 0 && l[0] != '#')
                .Select(l => l.Split('\t'))
                .Where(p => p.Length == 2)
                .Reverse()
                .ToList();
            foreach (var move in moves)
            {
                string source = move[0], target = move[1];
                try
                {
                    if (!File.Exists(target))
                        continue;
                    if (File.Exists(source))
                    {
                        log("Data migration rollback: " + source + " exists again - leaving " + target + " in place.");
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                    File.Move(target, source);
                    log("Data migration rollback: moved " + target + " back to " + source + ".");
                }
                catch (Exception ex)
                {
                    log("Data migration rollback: could not move " + target + " back to " + source + ": " + ex.Message);
                }
            }

            TryDelete(Path.Combine(dataDirectory, MarkerFileName));
            TryDelete(journalPath);
            log("Data migration rollback: done.");
        }

        private void MoveDatabases(string legacyDirectory, string dataDirectory, string journalPath, MigrationResult result)
        {
            foreach (var db in Directory.GetFiles(legacyDirectory, "*.db"))
            {
                var name = Path.GetFileName(db);
                // Directory.GetFiles("*.db") also matches "*.db-wal" etc. on some file systems (8.3 matching) - keep only
                // the database files themselves.
                if (!name.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                    continue;

                var group = new List<string> { name };
                group.AddRange(DatabaseSideFileSuffixes
                    .Select(suffix => name + suffix)
                    .Where(side => File.Exists(Path.Combine(legacyDirectory, side))));

                var conflict = group.FirstOrDefault(f => File.Exists(Path.Combine(dataDirectory, f)));
                if (conflict != null)
                {
                    log("Data migration: KEEPING the database in the data directory - " + Path.Combine(dataDirectory, conflict)
                        + " already exists. The legacy database " + db + " (with its side files) was NOT moved; review it by hand.");
                    result.Skipped.AddRange(group.Select(f => Path.Combine(legacyDirectory, f)));
                    continue;
                }

                // Side files first, the database last: if the run stops in between, the legacy folder still holds the
                // database and the rollback restores the side files next to it.
                foreach (var file in group.Skip(1).Concat(new[] { name }))
                    MoveFile(Path.Combine(legacyDirectory, file), Path.Combine(dataDirectory, file), journalPath, result);
            }

            // Orphaned side files (whose database is gone) and database backups ("*.db.bak-20260604") - move them along,
            // never overwrite. A side file whose database is still in the legacy folder (because it was kept there after
            // a conflict) stays with it.
            foreach (var file in Directory.GetFiles(legacyDirectory))
            {
                var name = Path.GetFileName(file);
                var suffix = DatabaseSideFileSuffixes.FirstOrDefault(s => name.EndsWith(".db" + s, StringComparison.OrdinalIgnoreCase));
                bool orphanedSideFile = suffix != null
                    && !File.Exists(Path.Combine(legacyDirectory, name.Substring(0, name.Length - suffix.Length)));
                bool backup = name.IndexOf(".db.bak", StringComparison.OrdinalIgnoreCase) >= 0;
                if (orphanedSideFile || backup)
                    MoveOrSkip(file, Path.Combine(dataDirectory, name), journalPath, result);
            }
        }

        private void MoveSingleFiles(string legacyDirectory, string dataDirectory, string journalPath, MigrationResult result)
        {
            var names = Directory.GetFiles(legacyDirectory, "appsettings*.json")
                .Select(Path.GetFileName)
                .Concat(SingleFileNames)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                var source = Path.Combine(legacyDirectory, name!);
                if (!File.Exists(source))
                    continue;
                var target = Path.Combine(dataDirectory, name!);
                if (MoveOrSkip(source, target, journalPath, result) && name!.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
                    MarkAsModified(target);
            }
        }

        private void MoveKeyRing(string legacyDirectory, string dataDirectory, string journalPath, MigrationResult result)
        {
            var legacyKeys = Path.Combine(legacyDirectory, KeysFolderName);
            if (!Directory.Exists(legacyKeys))
                return;

            var targetKeys = Path.Combine(dataDirectory, KeysFolderName);
            foreach (var file in Directory.GetFiles(legacyKeys, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(legacyKeys.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                MoveOrSkip(file, Path.Combine(targetKeys, relative), journalPath, result);
            }

            // Remove the now empty legacy key folder; a rollback recreates folders as it moves files back.
            try
            {
                foreach (var dir in Directory.GetDirectories(legacyKeys, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                if (!Directory.EnumerateFileSystemEntries(legacyKeys).Any())
                {
                    Directory.Delete(legacyKeys);
                    log("Data migration: removed the empty legacy folder " + legacyKeys + ".");
                }
            }
            catch (Exception ex)
            {
                log("Data migration: could not remove the legacy folder " + legacyKeys + ": " + ex.Message);
            }
        }

        private bool MoveOrSkip(string source, string target, string journalPath, MigrationResult result)
        {
            if (File.Exists(target))
            {
                log("Data migration: KEEPING " + target + " (already present in the data directory); the legacy file "
                    + source + " was NOT moved.");
                result.Skipped.Add(source);
                return false;
            }
            MoveFile(source, target, journalPath, result);
            return true;
        }

        private void MoveFile(string source, string target, string journalPath, MigrationResult result)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Journal first: if the move happens and the process dies before the next line, the rollback still knows it.
            File.AppendAllText(journalPath, source + "\t" + target + Environment.NewLine, Encoding.UTF8);
            File.Move(source, target);
            result.Moved.Add(target);
            log("Data migration: moved " + source + " -> " + target + ".");
        }

        // Windows Installer treats an unversioned file whose modification time is not later than its creation time as
        // "unmodified" and replaces it with the packaged default. A moved appsettings file is operator data, so its
        // creation time is put just before its last write: the installer then keeps it.
        private void MarkAsModified(string path)
        {
            try
            {
                var written = File.GetLastWriteTimeUtc(path);
                if (File.GetCreationTimeUtc(path) >= written)
                    File.SetCreationTimeUtc(path, written.AddSeconds(-2));
            }
            catch (Exception ex)
            {
                log("Data migration: could not adjust the timestamps of " + path + ": " + ex.Message);
            }
        }

        private void WriteMarker(string? legacyDirectory, string dataDirectory, MigrationResult result)
        {
            var text = new StringBuilder();
            text.AppendLine("WinSmtpRelay data migration");
            text.AppendLine("completed-utc: " + DateTime.UtcNow.ToString("o"));
            text.AppendLine("legacy-folder: " + (legacyDirectory ?? "(none)"));
            text.AppendLine("data-folder: " + dataDirectory);
            foreach (var moved in result.Moved)
                text.AppendLine("moved: " + moved);
            foreach (var skipped in result.Skipped)
                text.AppendLine("kept-in-legacy-folder: " + skipped);
            File.WriteAllText(Path.Combine(dataDirectory, MarkerFileName), text.ToString(), Encoding.UTF8);
            log("Data migration: marker written (" + Path.Combine(dataDirectory, MarkerFileName) + ").");
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort */ }
        }
    }

    /// <summary>Outcome of <see cref="LegacyDataMigrator.Migrate"/>.</summary>
    public sealed class MigrationResult
    {
        /// <summary>The marker existed: the migration had already run.</summary>
        public bool AlreadyMigrated { get; set; }

        /// <summary>Legacy and data directory are the same folder (development run): nothing to do.</summary>
        public bool SameDirectory { get; set; }

        /// <summary>Target paths of the files that were moved.</summary>
        public List<string> Moved { get; } = new List<string>();

        /// <summary>Legacy paths that were left in place because the data directory already had the file.</summary>
        public List<string> Skipped { get; } = new List<string>();
    }
}
