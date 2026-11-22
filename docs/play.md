# Play from Visual Studio

`Play/` is the startup project: F5 (debug) or Ctrl+F5 builds the engine and every mod, copies the mods into the game's mods folder and
runs `MeitouClient.exe` from `engine/src/Meitou.Game/bin/Release/<tfm>/` (its `log.txt` is there too). `dotnet run --project Play` does the
same from a terminal.

## What a play build does

- Builds `engine/src/Meitou.Game` and each mod in **Release**, whatever the solution configuration: Debug engine builds
  throw in `TaskStart.CheckStarts` once a mod starts a character's task. Breakpoints in mod code still work (Release symbols are deployed).
- Copies each mod's `<Mod>.dll`, `<Mod>.pdb` and `meitou.json` to `<mods folder>/<Mod>/`. Close the game first: a running game locks the dll.
- Each mod gets an empty `<Mod>.mod` when it has none; an existing one is never replaced.
- When the mods folder doesn't exist (CI, Linux) nothing is copied and the build says so.

A new mod is one `PlayMod` line in `Play/Play.csproj`.

## The Meitou launcher

The second launch profile, "Meitou launcher (installed)" (pick it in the drop-down beside the start button), builds and deploys the mods as
above, then opens the launcher installed from the Meitou releases (`%LOCALAPPDATA%\MeitouLauncher\current\meitou-launcher.exe`) instead of
the game. Use it to change the mod list or load order (`data/mods.cfg`) or to trust a new mod's code. Its Play button starts the release it
installed, not this checkout's engine: close it and use "Play (engine + mods)" to test. Visual Studio only; `dotnet run` uses the first profile.

## Settings

Defaults: game `C:\Program Files (x86)\Steam\steamapps\common\Kenshi`, mods folder `<game>\mods`. Change them with the `KENSHI_PATH`
environment variable (the engine reads it too, so both agree), or per machine in a git-ignored `MeitouMods.user.props` at the repo root:

```xml
<Project>
  <PropertyGroup>
    <KenshiDir>D:\Games\Kenshi</KenshiDir>           <!-- game folder; also sets the mods folder -->
    <KenshiModsDir>D:\Games\Kenshi\mods</KenshiModsDir> <!-- mods folder only -->
    <PlayConfiguration>Debug</PlayConfiguration>      <!-- engine and mods configuration (default Release) -->
    <PlayDeploy>false</PlayDeploy>                    <!-- build and run without copying -->
    <PlayArguments>--continue</PlayArguments>         <!-- engine command line -->
  </PropertyGroup>
</Project>
```

The engine finds the game on its own (Steam); `KenshiDir` set only in the props file isn't passed to it.

## First run

- Set `Play` as the startup project if Visual Studio didn't pick it (right-click > Set as Startup Project); its default launch profile is
  "Play (engine + mods)".
- Enable the mods in the game's launcher and trust their code in Meitou's launcher (docs/modding.md in the engine).
- Steam normally leaves its Program Files folder writable without admin (unchecked here). If the copy fails with "access denied",
  give your user Modify rights on `Kenshi\mods` (Properties > Security) rather than running Visual Studio as administrator.
