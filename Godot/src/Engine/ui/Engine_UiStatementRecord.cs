using System.Collections.Generic;
using System.IO;

namespace Refii.Ui;

/// <summary>
/// A RECORD OF EVERY UI STATEMENT THE HOST RECEIVES, to a file, while it is on: each `GUI.*` native
/// and `@…` post as <see cref="Engine_UiGuiNatives"/> dispatches it (<c>G</c>), each told slide,
/// pause, attribute, active and texture record (<c>S</c>) and each told text (<c>T</c>) as
/// <see cref="Engine_UiSlideStatements"/> takes them, and the debug channel's marks (<c>M</c>).
/// </summary>
/// <remarks>
/// A line is <c>frame kind text</c>. Presentations are named by their headless file, never by
/// address, so two runs of one route compare line for line. Main thread only.
/// </remarks>
public static class Engine_UiStatementRecord
{
	private static StreamWriter? _writer;

	private static string _path = string.Empty;

	private static long _lines;

	private static readonly Dictionary<(uint Presentation, uint Serial), string> Files = new();

	/// <summary>True while a record is being written.</summary>
	public static bool Recording => _writer is not null;

	/// <summary>The file being written, or the last one written.</summary>
	public static string Path => _path;

	/// <summary>Lines written to the current or last record.</summary>
	public static long Lines => _lines;

	/// <summary>Starts a record at <paramref name="path"/>, ending any record in progress.</summary>
	public static void Start(string path)
	{
		Stop();
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
		_writer = new StreamWriter(path, false);
		_path = path;
		_lines = 0;
		Files.Clear();
	}

	/// <summary>Ends the record in progress, if any.</summary>
	public static void Stop()
	{
		_writer?.Flush();
		_writer?.Dispose();
		_writer = null;
	}

	/// <summary>One line of <paramref name="kind"/>.</summary>
	public static void Note(char kind, string text)
	{
		if (_writer is null)
		{
			return;
		}

		_writer.Write(Godot.Engine.GetProcessFrames());
		_writer.Write(' ');
		_writer.Write(kind);
		_writer.Write(' ');
		_writer.WriteLine(text);
		++_lines;
	}

	/// <summary>A told record's presentation by its headless file (or "live" for a built one).</summary>
	public static string FileOf(uint presentation, uint root, uint ordinalWord)
	{
		if (!Engine_UiHeadlessElements.IsHeadless(ordinalWord))
		{
			return "live";
		}

		if (!Files.TryGetValue((presentation, root), out string? file))
		{
			string? told = Refii.Host.HostBridge.ToldHeadlessFile(presentation, root);
			file = told is null ? "?" : System.IO.Path.GetFileNameWithoutExtension(told.Replace('\\', '/'));
			Files[(presentation, root)] = file;
		}

		return file;
	}
}
