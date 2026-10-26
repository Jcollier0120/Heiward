// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

namespace HEI.Core.Utils {
	public enum LogSeverity {
		Info,
		Warning,
		Error,
	}

	/// <summary>
	/// One log line. <see cref="IsSessionStart"/> entries mark the beginning of a
	/// logical session (e.g. a scan); their <see cref="Message"/> is the session label.
	/// </summary>
	public readonly record struct LogEntry(DateTime Timestamp, LogSeverity Severity, string Message, bool IsSessionStart = false);

	public sealed class Logger {
		// Eagerly initialized: `instance ??= new()` raced under parallel first access,
		// briefly yielding TWO loggers — a subscriber on the first missed every entry
		// raised through the second.
		static readonly Logger instance = new();
		public static Logger Instance => instance;
		public event LogEventHandler? LogEntryAdded;
		public delegate void LogEventHandler(LogEntry entry);
		static readonly object lockObject = new();
		static readonly Lazy<string> _logFilePath = new(() =>
			Path.Combine(CoreUtils.IsCurrentFolderWritable
				? CoreUtils.CurrentFolder
				: CoreUtils.GetDefaultStateFolder(), "log.txt"));
		public static string LogFilePath => _logFilePath.Value;

		public void Info(string text) => Add(new LogEntry(DateTime.Now, LogSeverity.Info, text));
		public void Warn(string text) => Add(new LogEntry(DateTime.Now, LogSeverity.Warning, text));
		public void Error(string text) => Add(new LogEntry(DateTime.Now, LogSeverity.Error, text));
		/// <summary>Marks the start of a logical session (e.g. a scan) with a short label.</summary>
		public void BeginSession(string label) => Add(new LogEntry(DateTime.Now, LogSeverity.Info, label, IsSessionStart: true));

		/// <summary>The size a log grows to before <see cref="MoveAside"/> starts a new one.</summary>
		public const long FullLogBytes = 1 << 20;

		/// <summary>
		/// Moves a full log aside to its ".1" name (log.txt to log.1.txt, replacing the one there), so the next line starts
		/// a new log: two of them at most, instead of one that grows for years. Best-effort: a log that can't be moved now
		/// is moved on a later line.
		/// </summary>
		public static void MoveAside(string path) {
			string previous = Path.ChangeExtension(path, ".1" + Path.GetExtension(path));
			try {
				if (new FileInfo(path).Length <= 2 * FullLogBytes) {
					File.Move(path, previous, overwrite: true);
					return;
				}
				// A log from before logs were moved aside, hundreds of megabytes of it: only its last lines are kept.
				using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
					source.Seek(-FullLogBytes, SeekOrigin.End);
					using var target = new FileStream(previous, FileMode.Create, FileAccess.Write, FileShare.Read);
					source.CopyTo(target);
				}
				File.Delete(path);
			}
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}

		void Add(LogEntry entry) {
			LogEntryAdded?.Invoke(entry);
			lock (lockObject) {
				// Best-effort: a reader holding log.txt (tail, editor, antivirus) must
				// never turn a log call into an exception inside the scan engine.
				try {
					// Shared for deleting too: another process writing to it doesn't stop it being moved aside.
					FileStream Open() => new(_logFilePath.Value, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
					FileStream stream = Open();
					if (stream.Length >= FullLogBytes) {
						stream.Dispose();
						MoveAside(_logFilePath.Value);
						stream = Open();
					}
					using var writer = new StreamWriter(stream);
					writer.WriteLine(FormatFileLine(entry));
				}
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}

		// Info lines keep the historic "HH:mm:ss => text" shape; severity only shows
		// up as a tag when there is something to flag.
		internal static string FormatFileLine(LogEntry entry) => entry switch {
			{ IsSessionStart: true } => $"{Environment.NewLine}{new string('-', 60)} {entry.Message} · {entry.Timestamp:HH:mm:ss} {new string('-', 60)}",
			{ Severity: LogSeverity.Warning } => $"{entry.Timestamp:HH:mm:ss} => [WARNING] {entry.Message}",
			{ Severity: LogSeverity.Error } => $"{entry.Timestamp:HH:mm:ss} => [ERROR] {entry.Message}",
			_ => $"{entry.Timestamp:HH:mm:ss} => {entry.Message}",
		};
	}

}
