using AceLand.Injection;
using UnityEngine;

namespace AceLand.Sample.Injection.Scripts.Installers
{
    public class InfoUiInstaller : MonoInstaller
    {
        [SerializeField] private Transform container;
        
        [Header("Prefab")]
        [SerializeField] private PlayerInfoUi playerInfoUiPrefab;
        
        public override void Install(IContainerBuilder builder)
        {
            builder.RegisterPrefabPool(playerInfoUiPrefab, parent: container, prewarm: 8, maxSize: 256);
        }
    }
}