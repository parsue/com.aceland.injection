# AceLand Injection

[![Sponsor](https://img.shields.io/badge/Sponsor-%E2%9D%A4-db61a2?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/parsue)
[![Discord](https://img.shields.io/badge/Discord-Join-5865F2?logo=discord&logoColor=white)](https://discord.gg/XsCYGnYzuc)
[![Docs](https://img.shields.io/badge/Docs-GitBook-3884FF?logo=gitbook&logoColor=white)](https://docs.parsue.io/aceland-unity-packages)

A **lightweight**, zero-reflection dependency injection container built for modern Unity.

> ❤️ **Enjoying this package?** Consider [sponsoring on GitHub](https://github.com/sponsors/parsue). Sponsors get a community role on our [Discord](https://discord.gg/XsCYGnYzuc). (Sponsorship is a voluntary thank-you and is separate from the paid Editor tools / license.)

> 📖 **New here?** Read [**Why a New DI & Lifecycle Stack for Modern Unity**](https://docs.parsue.io/aceland-unity-packages/core-packages/why-a-new-di-and-lifecycle-stack-for-modern-unity) — the reasoning behind this package, an honest comparison vs Zenject/VContainer/Reflex, and when *not* to use it.

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

## How It Compares

Every framework below is good — they were simply designed for different constraints.
This table is honest positioning, not point-scoring.

| Concern | Zenject / Extenject | VContainer | Reflex | **AceLand Injection** |
|---|---|---|---|---|
| Resolution strategy | Reflection + codegen | IL/expression, low-alloc | Reflection, minimal | **Source-generated, zero reflection on hot path** |
| Runtime warm-up cost | Noticeable | Low | Low | **None (compile-time injectors)** |
| Mental model size | Large (sub-containers, bindings) | Medium | Small | **Small (Register/Resolve/Inject + scopes)** |
| Cross-package global container | Manual | Manual | Manual | **Built in (`DI`)** |
| Component wiring attributes | Partial | No | No | **`[Self]/[Parent]/[Child]/[FromScene]/[AddComponent]`** |
| No-domain-reload design | Retrofitted | Good | Good | **Designed for it from day one** |
| Build-time dependency validation | No | No | No | **Node graph + scene/prefab checks** |

**When _not_ to reach for it:** a tiny project where a couple of `[SerializeField]`
references are genuinely enough; a team deeply invested in a Zenject-style sub-container
architecture where migration cost outweighs the win; or a workflow still hard-locked to
domain reload with no plans to move.

## Feature Highlights

- **Global container** (`DI`) shared across packages — publish once, resolve anywhere.
- **Source-generated injectors** — zero-reflection fast path via a Roslyn incremental generator.
- **Component injection** — `[Self]`, `[Parent]`, `[Child]`, `[FromScene]`, `[AddComponent]`.
- **Scene scopes** — drop an `InjectionScope` on a GameObject for a child container per scene/prefab.
- **Lifecycle entry points** — `IInitializable` / `ITickable` / `IAsyncEntryPoint` driven by Unity's loop.
- **Object pooling** and **build-time validation** (graph window + scene/prefab checks).

## Pairs With AceLand Lifecycle

Injection answers *"how do objects find each other?"* — its companion package
[**AceLand Lifecycle**](https://docs.parsue.io/aceland-unity-packages/core-packages/lifecycle-exp)
answers *"in what order does the world come alive, and how does it shut down cleanly?"*
Together they form a modern, no-domain-reload architecture baseline. See the
[architecture article](https://docs.parsue.io/aceland-unity-packages/core-packages/why-a-new-di-and-lifecycle-stack-for-modern-unity)
for how they work as one story.

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

## Open Core

The **runtime is free and open source, forever** — the container, source-generated
injectors, pooling, and entry points are never gated, and **builds are never gated**.
Only the development-time Editor tools (the node-based dependency graph and scene/prefab
validation) require a paid AceLand license, and licensing is an **optional** dependency:
unlicensed editors keep the runtime fully functional and simply show a one-click install
prompt on the gated windows.

## Documents

We use GitBook as the public documentation for our packages.

> Visit our [GitBook](https://docs.parsue.io/aceland-unity-packages)

Please visit our GitBook for details.
