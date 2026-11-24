# IL Patch Studio

[简体中文](README.zh-CN.md)

IL Patch Studio is a Windows desktop tool for inspecting managed .NET assemblies and drafting C# patches for MonoMod. It pairs decompiled C# with the original IL so you can select an expression, a statement, or an instruction and review the generated `ILCursor` code. The application reads assemblies as metadata and does not execute or rewrite the assembly you open.

## Quick start

1. Extract the entire Windows x64 portable ZIP to a writable folder and launch `ILPatchStudio.exe`. The portable build includes the .NET desktop runtime.
2. Open or drag in [`samples/PriceDemo.dll`](samples/PriceDemo.dll) from the release. When working from source, build the sample using the command below.
3. Search for `Checkout.CalculateTotal`. Select `2.5f` in the C# pane, choose **Replace constant**, and enter `3.0f`. The IL pane shows the matched instruction.
4. Select the statement that multiplies the unit count by the unit price (shown as `float num3 = (float)num * num2;` in the bundled example). Choose **Insert C# after statement**, then open **Pass variables** and select the subtotal local as `ref`. In the patch editor, increment the displayed alias, for example `local2++;`.
5. Copy the draft into a class in your MonoMod-based project, place its `using` directives at file scope, and register the generated manipulator with the target method's IL hook.

## Drafts and dependencies

- Replace or transform constants and stack values. The generator recognizes integral and floating-point constants, strings, and C# bool or char literals where the decompiler identifies them.
- Insert C# before or after supported statements, calls, or instructions. Use the branch options to target a conditional jump or its fall-through path. A statement whose IL has ambiguous boundaries may require selecting the instruction directly.
- Transform return values, final call arguments, and field reads or writes. A selected local store can be redirected to another local of the same type.
- Pass variables by value or choose `ref`, `out`, or `in` where available. `ref` writes through to the original variable. `out` requires assignment on every normal return from the delegate. Confirm that any local read at the injection point is initialized.
- **Include using** controls whether imports appear in the preview and copied draft. When disabled, the tool lists required namespaces separately. Optional guards control the runtime behavior when the pattern is missing, duplicated, or near an injection boundary.
- The **Dependencies** tab compares each direct reference's requested identity with the file chosen by the resolver. The tooltip shows the requested and found identities and the resolved path. A version difference is diagnostic information: the resolver can still select that file. Add search folders when a reference is missing or when decompiled C# shows unknown types.

Opening assemblies and decompiling methods run in the background. When you switch methods quickly, the panes follow the most recent selection. The tool checks that a draft's chosen anchor uniquely matches the opened method body. Review and test the patch with the target version and other active hooks.

## Settings and sessions

Language, code font size, window layout, and draft preferences are saved in `data/settings.json` beside the executable. Dependency folders, search text, and the last selected method are tracked per assembly. Use **Recent** to switch assemblies and **Settings** to disable startup restoration. Keep the `data` folder when moving an installation if you want to carry its history with it.

## Build from source

Use Windows and the .NET 10 SDK. The default build restores pinned versions of ICSharpCode.Decompiler and Mono.Cecil from NuGet. For offline builds, you can set the optional `IlSpyDirectory` MSBuild property to a directory containing those same DLL versions.

```powershell
dotnet build ILPatchStudio.csproj -c Release
dotnet build samples/PriceDemo/PriceDemo.csproj -c Release
pwsh -File scripts/Publish-Portable.ps1
```

The sample build produces `samples/PriceDemo/bin/Release/net10.0/PriceDemo.dll`. The PowerShell 7 release script writes a fresh self-contained ZIP to `release/`, with the example and notices. It stages the release in a temporary folder, omits personal `data`, and reports the SHA-256 checksum.

## License

IL Patch Studio and the sample source use the [MIT License](LICENSE). Third-party components retain their own terms. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) and the runtime notices in the portable archive.
