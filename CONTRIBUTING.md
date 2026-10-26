# Contributing

Thanks for helping. Issues and pull requests are welcome at [XKD-Labs/simpledatalogs-unity](https://github.com/XKD-Labs/simpledatalogs-unity).

## Layout

| Folder | What |
|---|---|
| `Runtime/Core/` | The pure C# core: encoder, outbox, pipeline, retries, sessions. No `UnityEngine` references; C# 9 and .NET Standard 2.1 |
| `Runtime/Unity/` | The static facade, the hidden runner, the UnityWebRequest transport and the autocapture hooks |
| `Tests/Editor/`, `Tests/Runtime/` | Unity Test Framework tests (NUnit), run inside the editor or on a player |
| `Tests~/DotNet/` | .NET 8 NUnit tests for the core; Unity ignores folders ending in `~` |
| `Samples~/QuickStart/` | The sample the Package Manager imports |

## Run the .NET tests

You need only Docker; nothing is installed on your machine.

```sh
docker volume create sdl-unity-nuget
docker run --rm -v "$PWD":/w -v sdl-unity-nuget:/root/.nuget/packages -w "/w/Tests~/DotNet" mcr.microsoft.com/dotnet/sdk:8.0.414 dotnet test
```

The same build also:

- compiles `Runtime/Core` alone at C# 9 against .NET Standard 2.1 (`Tests~/DotNet/CoreCompat`), so nothing newer than Unity 2021.3 offers slips in;
- type-checks `Runtime/`, the sample and the Unity tests against minimal UnityEngine stubs (`Tests~/DotNet/UnityLayerCompile`).

The stubs only prove the code compiles; behaviour inside Unity is covered by the Unity tests below.

## Run the Unity tests

1. Add the package to a Unity 2021.3+ project from disk (Package Manager → **+** → "Add package from disk…", pick `package.json`).
2. List it as testable in the project's `Packages/manifest.json`: `"testables": ["com.xkdlabs.simpledatalogs"]`.
3. Open Window → General → Test Runner and run the **EditMode** and **PlayMode** tests.

To check IL2CPP or WebGL, run the PlayMode tests on that player from the Test Runner.

## Meta files

Unity needs a `.meta` file beside every file and folder in a git package. After adding a file, run:

```sh
python3 .github/scripts/unity_meta.py write
python3 .github/scripts/unity_meta.py check
```

GUIDs are derived from the path, so they are stable.

## Style

- Every class and public member has a one-line `///` summary; every private method and each step inside a longer method has a one-line `//` comment.
- Self-describing names; booleans start with `is`, `has`, `should` or `can`.
- Braces on every `if`; one blank line between logical steps.
- The core stays free of reflection, `dynamic`, `System.Text.Json` and threads, so it runs under IL2CPP and on WebGL.

## Commits

[Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/), e.g. `fix(core): keep the journal when sealing fails`. Update `CHANGELOG.md` for anything users notice.

## Releases

Bump `version` in `package.json` and move the changelog entry under the new version. Run the tests and `python3 .github/scripts/unity_meta.py check` locally, then push a tag `v<version>`; OpenUPM picks up the tag by itself. Nothing runs on GitHub; a GitHub release with the changelog section is optional and made by hand.
