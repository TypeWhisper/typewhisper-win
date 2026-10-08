# Shared plugin sources

Portable plugins never reference each other and share no runtime assembly besides the SDK.
The files in this directory keep that rule: a plugin links them as *source* and compiles them
into its own assembly, so two plugins that link the same file still ship two independent
copies of the code.

Link a file from a plugin project like this:

```xml
<Compile Include="../shared/PortableLocalization.cs" Link="Shared/PortableLocalization.cs" />
<Using Include="TypeWhisper.Plugin.Shared" />
```

Test projects link test sources the same way (`../../shared/tests/<file>`).

## Rules

- A shared file must not depend on any plugin: no plugin types, no plugin namespaces, no
  assumptions about a plugin's manifest, settings or files. The SDK and the base class library
  are the only dependencies.
- Types stay namespace-neutral: `namespace TypeWhisper.Plugin.Shared;` with `internal` types.
  Each consumer gets its own private copy of the type; the plugin's `InternalsVisibleTo` for its
  test project covers it.
- Behaviour that differs between plugins is a variation point, not a branch on the plugin name.
  `ProviderConnection` uses `partial` methods for this: every linking plugin implements them in
  its own `ProviderConnection.<Plugin>.cs` and keeps its provider-specific calls there.
- A change here is a change to every plugin that links the file. Find the consumers before
  editing:

  ```sh
  grep -l "shared/<file>" plugins/*/*.csproj plugins/*/Tests/*.csproj
  ```

  Build and test each of them. CI does the same: `eng/Get-ChangedPluginProjects.ps1` selects
  every package whose project links a changed file under `plugins/shared/`.
- Plugin versions follow behaviour, not file moves. Bump a plugin only when a change alters what
  it does.

## Not shared on purpose

- `TypeWhisper.Plugin.Script` keeps its own `PortableLocalization`: it falls back to the
  package's JSON strings when the host has no localization, which the shared helper does not.
- `TypeWhisper.Plugin.CloudflareAsr` keeps its own `ProviderConnection`: OAuth changes how the
  secret is loaded, cleared and replaced, so its activation flow is not the shared one.
