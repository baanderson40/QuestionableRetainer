# Questionable Retainer

## Build and tests

- Requires .NET 10 SDK and Dalamud API 15 development files for the plugin.
- Build: `dotnet restore QuestionableRetainer.sln`, then `dotnet build QuestionableRetainer.sln -c Release`.
- Focused plugin build: `dotnet build QuestionableRetainer/QuestionableRetainer.csproj -c Release --arch x64`.
- Dalamud loads plugin DLL from `D:\Coding\C#\Questionable Retainer\QuestionableRetainer\bin\x64\Release\QuestionableRetainer.dll`.
- Rebuild for Dalamud with `dotnet build QuestionableRetainer.sln -c Release`.
- Run tests with `dotnet run --project QuestionableRetainer.Tests/QuestionableRetainer.Tests.csproj -c Release`.
- Tests are a custom console executable. Do not use `dotnet test`; all registered tests run and no filter exists.
- Preserve `QuestionableRetainer/packages.lock.json`; no repository lint, format, or CI command exists.

## Architecture

- `QuestionableRetainer/Plugin.cs` is the entrypoint, composition root, and command boundary (`/questionableretainer`, `/qr`).
- `Automation/HandoffController.cs` owns the framework-tick state machine.
- Keep pure policy logic in `Automation/`; the test executable links selected policy sources directly rather than referencing the plugin project.
- `Integrations/` owns IPC/reflection boundaries for AutoRetainer, Questionable, Lifestream, Henchman, and YesAlready.
- `Windows/ConfigurationWindow.cs` owns ImGui configuration; `Configuration.cs` persists monitoring and recovery state through Dalamud.

Runtime requires AutoRetainer, Questionable Automatic mode, and Lifestream. Ocean Fishing additionally requires Henchman, AutoHook, and vnavmesh. Handoff logic must stay on the Dalamud framework thread and use UTC timing. Persisted recovery must not automatically restart Questionable after reload; changes to Questionable reflection boundaries require live integration validation.
