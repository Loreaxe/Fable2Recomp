using System;
using System.Text;

namespace Refii.Save;

/// <summary>
/// A SAVE'S OWN HEADER, <c>Headers/00000001/&lt;package&gt;.header</c>: the content's XCONTENT_DATA as the
/// guest created it — a device id, the content type, then the 128-character display name the title gave
/// the package (Fable 3 "Hero 4" for <c>Hero003</c>), UTF-16 big-endian. The display name is the save's
/// own name for itself, the one the console's dashboard showed. Pure.
/// </summary>
public static class Engine_SaveHeader
{
	private const int DisplayNameOffset = 4 + 4;
	private const int DisplayNameBytes = 256;

	/// <summary>The display name a header's bytes hold, or null when they hold none.</summary>
	public static string? DisplayName(ReadOnlySpan<byte> header)
	{
		if (header.Length < DisplayNameOffset + 2)
		{
			return null;
		}

		ReadOnlySpan<byte> field = header.Slice(DisplayNameOffset, Math.Min(DisplayNameBytes, header.Length - DisplayNameOffset));
		int length = 0;
		while (length + 1 < field.Length && (field[length] != 0 || field[length + 1] != 0))
		{
			length += 2;
		}

		string name = Encoding.BigEndianUnicode.GetString(field[..length]).Trim();
		return name.Length == 0 ? null : name;
	}
}
