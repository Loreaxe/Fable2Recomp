using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;

namespace Refii.Core;

/// <summary>
/// The instance uniforms a node's materials declare, read from the shaders' own source and its includes, so a copy
/// of a node's look (Refii.Entities.Engine_ViewTwin.CopyLook) names them without asking the rendering server.
/// </summary>
/// <remarks>
/// <para>NO SERVER LIST. <c>RenderingServer.InstanceGeometryGetShaderParameterList</c> has the render thread fill a
/// <c>List&lt;PropertyInfo&gt;</c> the caller owns. With Godot 4.7.1's separate render thread that list was written
/// after the caller had returned: the render thread crashed in the instance uniforms' property-list append
/// (<c>InstanceUniforms</c> lookup, <c>HashMap::operator[]</c>, then the list's tail store at <c>0x30(last)</c>) one
/// frame after the first per-view twin was made (int7/crash_1654.txt). So the names come from the shader code
/// here, and each value from <see cref="Engine_InstanceParams.Get"/> (the value held, else
/// <c>instance_geometry_get_shader_parameter</c>, which returns a value and so waits for it).</para>
/// <para>Each shader's names are parsed once (keyed on the shader and its code's length and hash) and include files
/// once by path. Main thread.</para>
/// </remarks>
public static class Engine_InstanceUniformNames
{
	private static readonly Regex Declaration = new(
		@"\binstance\s+uniform\s+(?:(?:highp|mediump|lowp)\s+)?[A-Za-z_][A-Za-z0-9_]*\s+([A-Za-z_][A-Za-z0-9_]*)",
		RegexOptions.Compiled);

	private static readonly Regex Include = new(@"#include\s+""([^""]+)""", RegexOptions.Compiled);

	private static readonly Dictionary<(ulong Shader, int Length, int Hash), StringName[]> ByShader = new();
	private static readonly Dictionary<string, string> IncludeCode = new(StringComparer.Ordinal);

	/// <summary>
	/// The instance uniform names declared in <paramref name="code"/> and the files it includes, in order of first
	/// declaration, none repeated. <paramref name="readInclude"/> answers an include path with its code (null when it
	/// cannot be read). Pure.
	/// </summary>
	public static List<string> Parse(string code, Func<string, string?> readInclude)
	{
		var names = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var visited = new HashSet<string>(StringComparer.Ordinal);
		Walk(code, readInclude, names, seen, visited, 0);
		return names;
	}

	private static void Walk(string code, Func<string, string?> readInclude, List<string> names, HashSet<string> seen,
		HashSet<string> visited, int depth)
	{
		if (depth > 16)
		{
			return;
		}

		foreach (Match line in Include.Matches(code))
		{
			string path = line.Groups[1].Value;
			if (visited.Add(path) && readInclude(path) is { } included)
			{
				Walk(included, readInclude, names, seen, visited, depth + 1);
			}
		}

		foreach (Match declared in Declaration.Matches(code))
		{
			string name = declared.Groups[1].Value;
			if (seen.Add(name))
			{
				names.Add(name);
			}
		}
	}

	/// <summary>The instance uniforms <paramref name="shader"/> declares (its code and includes). Main thread.</summary>
	public static StringName[] Of(Shader? shader)
	{
		if (shader is null || !GodotObject.IsInstanceValid(shader))
		{
			return Array.Empty<StringName>();
		}

		string code = shader.Code ?? string.Empty;
		var key = (shader.GetInstanceId(), code.Length, code.GetHashCode(StringComparison.Ordinal));
		if (ByShader.TryGetValue(key, out StringName[]? names))
		{
			return names;
		}

		List<string> parsed = Parse(code, ReadInclude);
		names = new StringName[parsed.Count];
		for (int i = 0; i < parsed.Count; i++)
		{
			names[i] = parsed[i];
		}

		ByShader[key] = names;
		return names;
	}

	/// <summary>
	/// Adds the instance uniforms every material <paramref name="node"/> draws with declares to
	/// <paramref name="into"/>: the override, the overlay, each surface's override or the mesh's own material.
	/// </summary>
	public static void OfNode(MeshInstance3D node, HashSet<StringName> into)
	{
		AddMaterial(node.MaterialOverride, into);
		AddMaterial(node.MaterialOverlay, into);
		if (node.Mesh is not { } mesh)
		{
			return;
		}

		int surfaces = mesh.GetSurfaceCount();
		for (int s = 0; s < surfaces; s++)
		{
			AddMaterial(node.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s), into);
		}
	}

	private static void AddMaterial(Material? material, HashSet<StringName> into)
	{
		for (int guard = 0; material is not null && guard < 8; guard++)
		{
			if (material is ShaderMaterial shaded)
			{
				foreach (StringName name in Of(shaded.Shader))
				{
					into.Add(name);
				}
			}

			material = material.NextPass;
		}
	}

	private static string? ReadInclude(string path)
	{
		if (IncludeCode.TryGetValue(path, out string? code))
		{
			return code;
		}

		code = ResourceLoader.Exists(path) && ResourceLoader.Load(path) is ShaderInclude include ? include.Code : null;
		if (code is null && FileAccess.FileExists(path))
		{
			code = FileAccess.GetFileAsString(path);
		}

		IncludeCode[path] = code ?? string.Empty;
		return code;
	}
}
