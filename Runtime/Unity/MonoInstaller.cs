// MonoInstaller.cs
using UnityEngine;

namespace AceLand.Injection
{
    public abstract class MonoInstaller : MonoBehaviour, IInstaller
    { public abstract void Install(IContainerBuilder builder); }

    public abstract class ScriptableObjectInstaller : ScriptableObject, IInstaller
    { public abstract void Install(IContainerBuilder builder); }
}