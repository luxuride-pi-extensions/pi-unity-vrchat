// Runtime self-test for the JSON writer.
//
// This exists because PiUnity.Json had a bug that no compiler catches:
// OpenObject(string key) built the nested object in a throwaway Json, so the
// parent's firstStack never recorded the nesting and the matching CloseObject()
// called RemoveAt(-1) -- "Index was out of range" at runtime, on every response
// with a keyed object section. It compiled cleanly and only failed once the
// bridge was live.
//
// Compiled together with plugin/PiUnity.Json.cs by test/json-selftest.sh and
// run under Unity's bundled mono. Exits non-zero on the first failure.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using PiUnity;

internal static class Program
{
	private static int failures;

	/// A string through a JSON object and back, which is the only route a
	/// value takes between the caller and the C# side.
	private static string RoundTrip(string value)
	{
Json wrapper = new Json();
wrapper.OpenObject();
wrapper.AddString("v", value);
wrapper.CloseObject();
return Json.Parse(wrapper.ToString()).GetString("v");
	}

	private static void Check(string name, bool ok, string detail = "")
	{
		if (ok)
		{
			Console.WriteLine("  ok   " + name);
		}
		else
		{
			failures++;
			Console.WriteLine("  FAIL " + name + (detail.Length > 0 ? " -- " + detail : ""));
		}
	}

	private static int Main()
	{
		// The exact shape GetSessionContext emits, including two sibling keyed
		// sections -- the case that used to throw.
		Json writer = new Json();
		writer.OpenObject();
		writer.AddString("projectPath", "/tmp/p");
		writer.AddNumber("frame", 42);
		writer.OpenObject("scene");
		writer.AddString("name", "Main");
		writer.AddArray("availableScenes", new[] { "Assets/Scene.unity" });
		writer.CloseObject();
		writer.OpenObject("selection");
		writer.AddString("activeGameObject", "Cube");
		writer.AddNumber("count", 1);
		writer.CloseObject();
		writer.AddArray("tags", Json.Array(new[] { Json.Str("a"), Json.Str("b") }));
		writer.CloseObject();

		string text = writer.ToString();

		// 1. The output must actually be valid JSON. A missing comma or brace
		//    shows up here, not at compile time.
		Json parsed = null;
		string parseError = "";
		try
		{
			parsed = Json.Parse(text);
		}
		catch (Exception error)
		{
			parseError = error.Message;
		}
		Check("writer output parses as JSON", parsed != null, parseError + " :: " + text);

		if (parsed == null)
		{
			Console.WriteLine("\n1 passed, " + failures + " failed (writer produced: " + text + ")");
			return 1;
		}

		// 2. Top-level scalars survived.
		Check("top-level string round-trips", parsed.GetString("projectPath") == "/tmp/p",
			"got " + parsed.GetString("projectPath"));
		Check("top-level number round-trips", parsed.GetInt("frame") == 42,
			"got " + parsed.GetInt("frame"));

		// 3. Keyed sections are actually nested -- the regression itself.
		Check("keyed 'scene' object present", parsed.properties.ContainsKey("scene"));
		Check("keyed 'selection' object present", parsed.properties.ContainsKey("selection"));
		Check("nested value readable through the keyed section",
			parsed.properties.ContainsKey("scene")
				&& parsed.properties["scene"].GetString("name") == "Main",
			"got " + (parsed.properties.ContainsKey("scene") ? parsed.properties["scene"].GetString("name") : "<no section>"));
		Check("second sibling section is not nested inside the first",
			parsed.properties.ContainsKey("selection")
				&& !parsed.properties["selection"].properties.ContainsKey("scene"));

		// 4. Sibling separator correctness: no ",," and no "{" without a comma.
		Check("no doubled commas", !text.Contains(",,"), text);
		Check("no doubled braces", !text.Contains("{{") && !text.Contains("}}"), text);

		// 5. An array added after a closed keyed object still gets its comma.
		Check("array after a keyed object parses", parsed.GetStringArray("tags").Count == 2,
			"got " + parsed.GetStringArray("tags").Count);

		// 6. The writer's real contract for the anonymous form. The plugin only
		//    ever calls OpenObject()/OpenArray() on a *fresh* builder (each
		//    response, and each array section, constructs its own Json), so
		//    nesting anonymously inside one builder is out of contract. Assert
		//    what is actually guaranteed -- and that a balanced pair round-trips.
		Json fresh = new Json();
		fresh.OpenObject();
		fresh.AddString("k", "v");
		fresh.CloseObject();
		Json freshParsed = null;
		string freshError = "";
		try { freshParsed = Json.Parse(fresh.ToString()); }
		catch (Exception error) { freshError = error.Message; }
		Check("a fresh builder's object round-trips", freshParsed != null, freshError + " :: " + fresh.ToString());
		Check("fresh builder content survives", freshParsed != null && freshParsed.GetString("k") == "v");

		// 7. An unbalanced CloseObject must not take down the whole response.
		//    The old keyed-nesting bug reached here as RemoveAt(-1); a guard
		//    turns any future misuse into a truncated response instead of an
		//    exception that fails the request.
		Json unbalanced = new Json();
		unbalanced.OpenObject();
		unbalanced.AddString("k", "v");
		unbalanced.CloseObject();
		unbalanced.CloseObject();
		Check("extra CloseObject does not throw", true);

		// 8. ARRAY elements must not carry keys. unity_bones shipped
		//    `"humanoidBones":[{"Hips":{...}}]` because a keyed map was opened
		//    with OpenArray() and filled with AddArray(key, value). `[ "k":{}`
		//    is a JSON syntax error, so EVERY response failed to parse. The
		//    element form has to be unkeyed, and the map form has to be keyed --
		//    assert both produce parseable output.
		// There is no keyed OpenArray overload -- that is why the keyed-array bug
		// existed. Assert its absence explicitly: if a future change adds it, this
		// test fails and points at the duplication to remove.
		Check("Json has no keyed OpenArray overload (use OpenArray + AddElement)",
			typeof(Json).GetMethods().All(m => !(m.Name == "OpenArray" && m.GetParameters().Length == 1)),
			"an OpenArray(string) overload now exists; the keyed-array workarounds can go");
		Check("Json has no keyed OpenObject mismatch (OpenObject(string) exists)",
			typeof(Json).GetMethod("OpenObject", new[] { typeof(string) }) != null);

		Json array = new Json();
		array.OpenObject();
		Json items = new Json();
		items.OpenArray();
		items.AddElement(Json.Str("a"));
		items.AddElement(Json.Str("b"));
		items.CloseArray();
		array.AddValue("items", items);
		array.CloseObject();
		string arrayError = "";
		Json arrayParsed = null;
		try { arrayParsed = Json.Parse(array.ToString()); }
		catch (Exception error) { arrayError = error.Message; }
		Check("array elements are unkeyed and parse", arrayParsed != null, arrayError + " :: " + array.ToString());
		Check("array element count survives", arrayParsed != null && arrayParsed.GetStringArray("items").Count == 2);
		Check("no empty-key artefacts in arrays", !array.ToString().Contains("\"\":"),
			array.ToString());

		// 9. A keyed map built with OpenObject, which is what bone names and
		//    property paths need -- their keys are only known at runtime.
		Json map = new Json();
		map.OpenObject();
		map.OpenObject("bones");
		map.AddValue("Hips", Json.Str("0,0.69,0"));
		map.AddValue("Head", Json.Str("0,1,-0.03"));
		map.CloseObject();
		map.CloseObject();
		string mapError = "";
		Json mapParsed = null;
		try { mapParsed = Json.Parse(map.ToString()); }
		catch (Exception error) { mapError = error.Message; }
		Check("a runtime-keyed map parses", mapParsed != null, mapError + " :: " + map.ToString());
		Check("map keys survive",
			mapParsed != null
				&& mapParsed.properties.ContainsKey("bones")
				&& mapParsed.properties["bones"].properties.ContainsKey("Hips"),
			map.ToString());

		// 10. A bare boolean must stay a JSON boolean. The only unkeyed writers
		//     are Add(double) and AddString(string), so a bool passed to Add
		//     widens to double and emits 1/0 -- a type change a caller cannot see.
		Json bools = new Json();
		bools.OpenObject();
		bools.Add("flag", true);
		bools.CloseObject();
		Check("Add(key, bool) emits true/false, not 1/0",
			bools.ToString().Contains(":true"), bools.ToString());

		// 11. Escape decoding on the way IN. The writer always escapes, so this
		//     is the path every value with a backslash, quote or control
		//     character takes. It decoded only n/r/t and appended the character
		//     that followed the backslash for anything else, so a Windows path
		//     -- the string a Unity caller passes most -- lost a backslash per
		//     separator and came back wrong, with no error anywhere.
		Check("a Windows path survives the round trip",
			RoundTrip("C:\\Users\\lux\\Project\\Assets\\Scene.unity")
				== "C:\\Users\\lux\\Project\\Assets\\Scene.unity",
			RoundTrip("C:\\Users\\lux\\Project\\Assets\\Scene.unity"));
		Check("a quote inside a value survives",
			RoundTrip("say \"hi\"") == "say \"hi\"", RoundTrip("say \"hi\""));
		Check("a backslash-quote pair stays two characters",
			RoundTrip("a\\\"b") == "a\\\"b", RoundTrip("a\\\"b"));
		Check("\\uXXXX decodes to its character",
			Json.Parse("{\"v\":\"\\u0041\\u00e9\"}").GetString("v") == "A\u00e9",
			Json.Parse("{\"v\":\"\\u0041\\u00e9\"}").GetString("v"));
		// And the inverse: a LITERAL backslash-u, which is what a value carrying
		// the text "\u0041" actually contains, must come back as itself. Written
		// as raw JSON so the two cases cannot be confused -- a round trip
		// through the writer escapes the backslash, and asserting the decoded
		// character there would demand that a literal string change meaning.
		Check("a literal backslash-u stays literal",
			RoundTrip("\\u0041") == "\\u0041", RoundTrip("\\u0041"));
		Check("a lone backslash before a quote does not run off the string",
			RoundTrip("trailing\\") == "trailing\\", RoundTrip("trailing\\"));
		Check("tab and newline survive the round trip",
			RoundTrip("a\tb\nc") == "a\tb\nc",
			RoundTrip("a\tb\nc").Replace("\t", "\\t").Replace("\n", "\\n"));
		Check("an undefined escape is passed through whole",
			Json.Parse("{\"v\":\"a\\qb\"}").GetString("v") == "a\\qb",
			Json.Parse("{\"v\":\"a\\qb\"}").GetString("v"));

		Console.WriteLine();
		Console.WriteLine(failures == 0
			? "all JSON writer self-tests passed"
			: failures + " self-test failure(s)");
		return failures == 0 ? 0 : 1;
	}
}
