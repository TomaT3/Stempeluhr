# Project Agent Instructions

## Skill Routing

- When working on Angular client code under `stempeluhr-client/`, use the official Angular skills:
  - `angular-developer` for Angular components, services, routing, forms, signals, dependency injection, styling, testing, accessibility, SSR, or CLI/tooling guidance.
  - `angular-new-app` only when creating or restructuring a new Angular application.
- When working on the .NET server code under `Stempeluhr.Api/` or solution-level .NET files such as `Stempeluhr.slnx`, `Directory.Build.props`, or `*.csproj`, use the official `dotnet/skills` skills that match the task:
  - `dotnet-webapi` for ASP.NET Core API, endpoints, middleware, request/response handling, or server-side application structure.
  - `run-tests`, `dotnet-test-frameworks`, `writing-mstest-tests`, and related test skills for .NET test work.
  - `msbuild-server`, `binlog-failure-analysis`, `resolve-project-references`, and related MSBuild skills for build or project-file work.
  - `convert-to-cpm` for NuGet Central Package Management or package version alignment work.
  - `csharp-scripts` for C# scripting tasks.

Always read the selected skill's `SKILL.md` before making related code changes.
