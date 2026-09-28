/**
 * One-off: split PiUnityBridge.cs into a directory of partial-class files.
 * Runs once; the result is the maintained form afterwards.
 */
import { readFileSync, writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const SRC = join(HERE, "PiUnityBridge.cs");
const OUT = join(HERE, "plugin");
const lines = readFileSync(SRC, "utf8").split("\n");

// 1-indexed inclusive ranges taken from the section banners in the file.
const PARTS = [
	["PiUnityBridge.Core.cs", "Bootstrap and the request poll loop", 43, 103],
	["PiUnityBridge.Polling.cs", "Polling, response writing, and C# snippet compilation", 105, 386],
	["PiUnityBridge.Dispatch.cs", "Request dispatch", 388, 445],
	["PiUnityBridge.Session.cs", "Live session state", 446, 501],
	["PiUnityBridge.SceneInfo.cs", "Structured scene inspection and compile status", 502, 707],
	["PiUnityBridge.Scenes.cs", "Opening and reloading scenes", 708, 767],
	["PiUnityBridge.Render.cs", "Camera renders, viewport capture, transform tool", 768, 878],
	["PiUnityBridge.Interchange.cs", "FBX import and OBJ export for the binder", 879, 1082],
	["PiUnityBridge.EditorApi.cs", "Reflective access to internal UnityEditor APIs", 1083, 1185],
];

const USINGS = `using System;
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
using UnityEngine.SceneManagement;`;

const TOP = `// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies, which is what lets it drop into any Unity 2021+ project.
`;

rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

for (const [file, title, from, to] of PARTS) {
	const body = lines.slice(from - 1, to);
	// Drop a leading section banner; the file header replaces it.
	while (body.length > 0 && /^\s*\/\/ -{3,}/.test(body[0])) body.shift();
	while (body.length > 0 && body[0].trim() === "") body.shift();
	while (body.length > 0 && body[body.length - 1].trim() === "") body.pop();

	// Body lines already carry their full namespace+class indentation from the
	// original file, so they are emitted verbatim -- adding a tab here would
	// over-indent every member by one level.
	const text = `${TOP}
// ${title}
//
${USINGS}

namespace PiUnity
{
\tpublic static partial class Bridge
\t{
${body.join("\n")}
\t}
}
`;
	writeFileSync(join(OUT, file), text, "utf8");
	console.log(`${file.padEnd(34)} ${String(body.length).padStart(4)} lines   first: ${body[0]?.trim().slice(0, 58)}`);
}

// Json is its own class, not part of the partial Bridge.
const jsonBody = lines.slice(1191 - 1, 1473);
writeFileSync(
	join(OUT, "PiUnity.Json.cs"),
	`${TOP}
// Minimal JSON writer/reader.
//
// Unity's JsonUtility cannot express the heterogeneous shapes the bridge
// returns (optional keys, arrays of mixed primitives, dictionaries), so the
// plugin carries its own. Hand-rolled rather than NuGet because the plugin must
// compile in a project whose package set pi does not control.
${USINGS}

namespace PiUnity
{
${jsonBody.join("\n")}
}
`,
	"utf8",
);
console.log(`${"PiUnity.Json.cs".padEnd(34)} ${String(jsonBody.length).padStart(4)} lines   first: ${jsonBody[0]?.trim().slice(0, 58)}`);

rmSync(SRC, { force: true });
console.log("\nsource removed; plugin/ is now the maintained form");
