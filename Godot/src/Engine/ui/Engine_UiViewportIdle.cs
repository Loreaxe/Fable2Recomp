namespace Refii.Ui;

/// <summary>
/// When a UI SubViewport that idles while it has nothing to show must still render.
/// </summary>
/// <remarks>
/// AN IDLE VIEWPORT KEEPS ITS LAST PICTURE (SubViewport.UpdateMode.Disabled), and the container over
/// it keeps drawing that picture. Idled in the same frame its content went, the picture kept is the
/// last one WITH the content. So the viewport renders while there is content and ONCE MORE after it
/// went: the picture it keeps is an empty one. While held idle from outside (the loading cover is
/// above it) nothing renders and the owed frame stands, so the first frame after the hold renders
/// what is there then. For a viewport whose container cannot simply be hidden with it.
/// </remarks>
public sealed class Engine_UiViewportIdle
{
	/// <summary>Whether the frame the viewport last rendered held content.</summary>
	private bool _drewContent;

	/// <summary>
	/// Whether the viewport renders this frame: <paramref name="held"/> idles it whatever it shows,
	/// <paramref name="content"/> is whether there is anything to draw now.
	/// </summary>
	public bool Renders(bool held, bool content)
	{
		bool renders = !held && (content || _drewContent);
		if (renders)
		{
			_drewContent = content;
		}

		return renders;
	}
}
