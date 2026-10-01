// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies, which is what lets it drop into any Unity 2021+ project.

// Minimal JSON writer/reader.
//
// Unity's JsonUtility cannot express the heterogeneous shapes the bridge
// returns (optional keys, arrays of mixed primitives, dictionaries), so the
// plugin carries its own. Hand-rolled rather than NuGet because the plugin must
// compile in a project whose package set pi does not control.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	internal sealed class Json
	{
		private readonly StringBuilder buffer = new StringBuilder();
		private readonly List<bool> firstStack = new List<bool>();

		public Json() { }

		public static Json Parse(string text)
		{
			int index = 0;
			return ParseValue(text, ref index);
		}

		public static Json Str(string value)
		{
			Json json = new Json();
			json.buffer.Append(Quote(value));
			return json;
		}

		/// A bare boolean literal. Needed because the only unkeyed writers are
		/// Add(double) and AddString(string): passing a bool to Add widens to
		/// double and emits 1/0 instead of true/false, which silently changes the
		/// type a caller reads back.
		public static Json Bool(bool value)
		{
			Json json = new Json();
			json.buffer.Append(value ? "true" : "false");
			return json;
		}

		public static Json Array(Json[] items)
		{
			Json json = new Json();
			json.buffer.Append('[');
			for (int i = 0; i < items.Length; i++)
			{
				if (i > 0) json.buffer.Append(',');
				json.buffer.Append(items[i].ToString());
			}
			json.buffer.Append(']');
			return json;
		}

		public static Json Object(Json[] pairs)
		{
			Json json = new Json();
			json.buffer.Append('{');
			for (int i = 0; i < pairs.Length; i++)
			{
				if (i > 0) json.buffer.Append(',');
				json.buffer.Append(pairs[i].ToString());
			}
			json.buffer.Append('}');
			return json;
		}

		// Writer ----------------------------------------------------------------

		public Json Add(string key, bool value)
		{
			Separator();
			buffer.Append(Quote(key)).Append(':').Append(value ? "true" : "false");
			return this;
		}

		public Json AddString(string key, string value)
		{
			Separator();
			buffer.Append(Quote(key)).Append(':').Append(Quote(value));
			return this;
		}

		/// One numeric overload only: int and float call sites both widen to
		/// double, so the C# compiler never has to break a tie between two
		/// candidates at a hundred call sites.
		public Json AddNumber(string key, double value)
		{
			Separator();
			buffer.Append(Quote(key)).Append(':').Append(value.ToString("R", CultureInfo.InvariantCulture));
			return this;
		}

		public Json AddBool(string key, bool value)
		{
			return Add(key, value);
		}

		public Json Add(string key, Json value)
		{
			Separator();
			buffer.Append(Quote(key)).Append(':').Append(value.ToString());
			return this;
		}

		/// Appends a prebuilt value as an ARRAY element, with no key.
		///
		/// Needed because Add(key, Json) always writes a key, and writing
		/// Add(key, value) with an empty key inside an array produces `"":{...}`,
		/// which is a JSON syntax error. An array's elements are positional, so
		/// there is nothing to name them by.
		public Json AddElement(Json value)
		{
			Separator();
			buffer.Append(value.ToString());
			return this;
		}

		/// Alias kept for readability at call sites that nest a prebuilt Json.
		public Json AddValue(string key, Json value)
		{
			return Add(key, value);
		}

		public Json AddArray(string key, Json value)
		{
			return Add(key, value);
		}

		public Json AddArray(string key, string[] values)
		{
			Json array = new Json();
			array.OpenArray();
			foreach (string value in values) array.AddString(value);
			array.CloseArray();
			return Add(key, array);
		}

		// Reader helpers -------------------------------------------------------

		public string GetString(string key)
		{
			Json value = Get(key);
			return value == null ? null : value.asString;
		}

		public bool GetBool(string key)
		{
			Json value = Get(key);
			if (value == null) return false;
			bool parsed;
			return bool.TryParse(value.asString, out parsed) && parsed;
		}

		public int GetInt(string key, int fallback = 0)
		{
			Json value = Get(key);
			if (value == null) return fallback;
			int parsed;
			return int.TryParse(value.asString, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
		}

		/// Float, because NumberStyles.Integer REFUSES a decimal point: GetInt
		/// silently returns the fallback for "12.5" rather than failing, so a
		/// fractional request through a caller that asked for an int lands as a
		/// default and the caller never learns why.
		public float GetFloat(string key, float fallback = 0)
		{
			Json value = Get(key);
			if (value == null) return fallback;
			float parsed;
			return float.TryParse(value.asString, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
		}

		public List<string> GetStringArray(string key)
		{
			Json value = Get(key);
			List<string> result = new List<string>();
			if (value == null) return result;
			foreach (Json item in value.items) result.Add(item.asString);
			return result;
		}

		private Json Get(string key)
		{
			return properties.TryGetValue(key, out Json value) ? value : null;
		}

		// Building -------------------------------------------------------------

		// OpenObject/OpenArray MUST call Separator() first.
		//
		// Without it, an array filled by building elements inline --
		//   array.OpenArray(); array.OpenObject(); ...CloseObject(); array.OpenObject();
		// -- emits no comma between elements, producing
		//   [{...}{...}...] which is a JSON syntax error. Separator() marks the
		//   PARENT frame (the one on top of the stack at entry), which is exactly
		//   the frame whose "have I written a member yet" flag needs updating.
		//
		// This silently broke unity_scene_info: the objects/materials/cameras/
		// lights/packages/assets arrays all build elements inline and all
		// produced unparseable JSON. Nothing catches it at compile time, and the
		// bridge reports ok:true because the response is written, not validated.
		public Json OpenObject()
		{
			Separator();
			buffer.Append('{');
			firstStack.Add(true);
			return this;
		}

		/// Keyed form. This MUST write into this buffer and push onto this
		/// stack, exactly like the anonymous form: the previous implementation
		/// built the nested object in a throwaway Json, so only a lone '{' landed
		/// in the parent while the parent's firstStack never learned about the
		/// nesting. The matching CloseObject() then called RemoveAt(-1) and threw
		/// "Index was out of range", which made every keyed section (scene,
		/// selection, viewport, tools, renderSettings) malformed on the wire.
		public Json OpenObject(string key)
		{
			Separator();
			buffer.Append(Quote(key)).Append(':').Append('{');
			firstStack.Add(true);
			return this;
		}
		public Json CloseObject() { Close('}'); return this; }
		public Json OpenArray()
		{
			Separator();
			buffer.Append('[');
			firstStack.Add(true);
			return this;
		}
		public Json CloseArray() { Close(']'); return this; }

		/// Balanced-close guard. RemoveAt(-1) used to throw "Index was out of
		/// range" and fail the whole request; the keyed-nesting bug reached here
		/// that way. An unbalanced close is a programming error, but it should
		/// degrade to a truncated response rather than an exception the caller
		/// has to diagnose from a stack trace inside the Editor.
		private void Close(char character)
		{
			if (firstStack.Count == 0) return;
			buffer.Append(character);
			firstStack.RemoveAt(firstStack.Count - 1);
		}

		public Json Add(double value)
		{
			Separator();
			buffer.Append(value.ToString("R", CultureInfo.InvariantCulture));
			return this;
		}

		public Json AddString(string value)
		{
			Separator();
			buffer.Append(Quote(value));
			return this;
		}

		private void Separator()
		{
			if (firstStack.Count == 0) return;
			int top = firstStack.Count - 1;
			if (firstStack[top]) firstStack[top] = false;
			else buffer.Append(',');
		}

		public override string ToString() { return buffer.ToString(); }

		public static string Quote(string value)
		{
			if (value == null) return "null";
			var builder = new StringBuilder(value.Length + 2);
			builder.Append('"');
			foreach (char character in value)
			{
				switch (character)
				{
					case '"': builder.Append("\\\""); break;
					case '\\': builder.Append("\\\\"); break;
					case '\n': builder.Append("\\n"); break;
					case '\r': builder.Append("\\r"); break;
					case '\t': builder.Append("\\t"); break;
					default:
						if (character < 0x20) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
						else builder.Append(character);
						break;
				}
			}
			builder.Append('"');
			return builder.ToString();
		}

		// Parsed state ---------------------------------------------------------

		public string asString;
		public List<Json> items = new List<Json>();
		public Dictionary<string, Json> properties = new Dictionary<string, Json>(StringComparer.Ordinal);

		private static void SkipWhitespace(string text, ref int index)
		{
			while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
		}

		private static Json ParseValue(string text, ref int index)
		{
			SkipWhitespace(text, ref index);
			if (index >= text.Length) throw new Exception("Unexpected end of JSON input.");
			char character = text[index];
			if (character == '{') return ParseObject(text, ref index);
			if (character == '[') return ParseArray(text, ref index);
			if (character == '"')
			{
				Json value = new Json();
				index++;
				var builder = new StringBuilder();
				while (index < text.Length && text[index] != '"')
				{
					if (text[index] == '\\' && index + 1 < text.Length)
					{
						// Decode the escape, and keep what it stands for rather than
						// the character that introduced it.
						//
						// The previous loop appended `escaped` for anything that was
						// not n/r/t, so \" came back as a bare quote, \\ as a single
						// backslash, and \u0041 as the four literal characters
						// "u0041". A Windows path in a value -- the one string a
						// Unity caller passes most often -- is full of backslashes,
						// so a doubled separator silently collapsed to one and
						// every subsequent \X lost its backslash. The result parsed,
						// because the parser never validates, and the error surfaced
						// as a path that simply did not exist.
						char escaped = text[index + 1];
						index += 2;
						switch (escaped)
						{
							case 'n': builder.Append('\n'); break;
							case 'r': builder.Append('\r'); break;
							case 't': builder.Append('\t'); break;
							case 'b': builder.Append('\b'); break;
							case 'f': builder.Append('\f'); break;
							case '/': builder.Append('/'); break;
							case '\\': builder.Append('\\'); break;
							case '"': builder.Append('"'); break;
							case 'u':
								{
									// \uXXXX, with a surrogate pair left to the next
									// escape so an astral character survives intact.
									if (index + 4 <= text.Length &&
										int.TryParse(text.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
									{
										builder.Append((char)code);
										index += 4;
									}
									else builder.Append('u');
									break;
								}
							// An escape JSON does not define is passed through
							// whole, backslash included, so the value round-trips
							// instead of losing a character.
							default: builder.Append('\\').Append(escaped); break;
						}
					}
					else builder.Append(text[index++]);
				}
				index++;
				value.asString = builder.ToString();
				return value;
			}
			int start = index;
			while (index < text.Length && ",}] \t\r\n".IndexOf(text[index]) < 0) index++;
			Json scalar = new Json();
			scalar.asString = text.Substring(start, index - start);
			return scalar;
		}

		private static Json ParseObject(string text, ref int index)
		{
			Json value = new Json();
			index++;
			SkipWhitespace(text, ref index);
			if (index < text.Length && text[index] == '}') { index++; return value; }
			while (index < text.Length)
			{
				SkipWhitespace(text, ref index);
				if (text[index] != '"') throw new Exception("Expected a JSON object key at offset " + index + ".");
				Json keyValue = ParseValue(text, ref index);
				SkipWhitespace(text, ref index);
				if (text[index] != ':') throw new Exception("Expected ':' at offset " + index + ".");
				index++;
				value.properties[keyValue.asString] = ParseValue(text, ref index);
				SkipWhitespace(text, ref index);
				if (index < text.Length && text[index] == ',') { index++; continue; }
				if (index < text.Length && text[index] == '}') { index++; break; }
				throw new Exception("Malformed JSON object near offset " + index + ".");
			}
			return value;
		}

		private static Json ParseArray(string text, ref int index)
		{
			Json value = new Json();
			index++;
			SkipWhitespace(text, ref index);
			if (index < text.Length && text[index] == ']') { index++; return value; }
			while (index < text.Length)
			{
				value.items.Add(ParseValue(text, ref index));
				SkipWhitespace(text, ref index);
				if (index < text.Length && text[index] == ',') { index++; continue; }
				if (index < text.Length && text[index] == ']') { index++; break; }
				throw new Exception("Malformed JSON array near offset " + index + ".");
			}
			return value;
		}
	}
}
