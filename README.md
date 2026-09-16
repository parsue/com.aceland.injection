# AceLand Injection

[![Sponsor](https://img.shields.io/badge/Sponsor-%E2%9D%A4-db61a2?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/parsue)
[![Discord](https://img.shields.io/badge/Discord-Join-5865F2?logo=discord&logoColor=white)](https://discord.gg/XsCYGnYzuc)
[![Docs](https://img.shields.io/badge/Docs-GitBook-3884FF?logo=gitbook&logoColor=white)](https://docs.parsue.io/aceland-unity-packages)

A **lightweight**, zero-reflection dependency injection container built for modern Unity.

> ❤️ **Enjoying this package?** Consider [sponsoring on GitHub](https://github.com/sponsors/parsue). Sponsors get a community role on our [Discord](https://discord.gg/XsCYGnYzuc). (Sponsorship is a voluntary thank-you and is separate from the paid Editor tools / license.)

## Why AceLand Injection

Most DI frameworks for Unity grew heavy over the years — deep container hierarchies,
reflection-driven resolution that costs you frames on first access, generated code you
can't see, and a mental model that takes a weekend to learn. AceLand Injection is a
deliberate reset for the **no-domain-reload / CoreCLR era**: fast, small, and boring in
the best possible way.

- **No reflection on the hot path.** A Roslyn incremental generator emits plain,
  readable injector code at compile time. Resolution is direct method calls — not
  `Activator.CreateInstance`, not cached `MethodInfo`. You pay nothing at runtime for
  the convenience of `[Inject]`.
- **One global container, shared across packages.** Publish a service once with `DI`
  and resolve it from anywhere — no need to thread a container reference through every
  constructor or re-register the same service in ten scenes.
- **A tiny surface you can hold in your head.** `Register`, `Resolve`, `Inject`,
  a handful of `Lifetime` values, and scene-level `InjectionScope`. That's the whole
  model. No sub-container ceremony to wire up a simple game.
- **Survives no-domain-reload.** Designed from day one for Unity's fast enter-play
  and CoreCLR direction — no static state that rots between play sessions, no
  reload-dependent bootstrapping.
- **Fails at build time, not at 2 a.m.** A node-based graph window plus scene/prefab
  validation surface missing or circular dependencies before you ship, instead of as
  a `NullReferenceException` in the field.

Contracts live in the companion `com.aceland.injection.abstractions` package, so a
library can declare its DI surface without taking a dependency on the runtime — keeping
your own packages just as lightweight.

## Feature Highlights

- **Global container** (`DI`) shared across packages — publish once, resolve anywhere.
- **Source-generated injectors** — zero-reflection fast path via a Roslyn incremental generator.
- **Component injection** — `[Self]`, `[Parent]`, `[Child]`, `[FromScene]`, `[AddComponent]`.
- **Scene scopes** — drop an `InjectionScope` on a GameObject for a child container per scene/prefab.
- **Lifecycle entry points** — `IInitializable` / `ITickable` / `IAsyncEntryPoint` driven by Unity's loop.
- **Object pooling** and **build-time validation** (graph window + scene/prefab checks).

## Quick Start

```csharp
using AceLand.Injection;

// 1. Publish a service to the global container
[AutoInstall]
public sealed class GameInstaller : IGlobalInstaller
{
    public void Install(IContainerBuilder builder)
    {
        builder.Register<IScoreService, ScoreService>(Lifetime.Singleton);
        builder.AddEntryPoint<GameLoop>();
    }
}

// 2. Consume it anywhere
public class Hud : MonoBehaviour
{
    [Inject] private IScoreService _score;   // injected by an InjectionScope

    void Awake() => DI.Inject(this);          // or resolve directly: DI.Resolve<IScoreService>()
}
```

No container to pass around, no reflection to warm up — the injector for `Hud` was
generated at compile time.

## Documents

We use GitBook as the public documentation for our packages.

> Visit our [GitBook](https://docs.parsue.io/aceland-unity-packages)

Please visit our GitBook for details.
