using AceLand.Injection;
using AceLand.Sample.Injection.Scripts.Models;
using UnityEngine;

namespace AceLand.Sample.Injection.Scripts.Profiles
{
    [CreateAssetMenu(fileName = "PlayerProfile", menuName = "AceLand/Sample/Injection/Player Profile")]
    public class PlayerProfile : ScriptableObjectInstaller
    {
        [Header("Player Data")]
        [SerializeField] private PlayerData playerData;

        private void OnValidate()
        {
            playerData.Validate();
        }

        public override void Install(IContainerBuilder builder)
        {
            builder.RegisterInstance(playerData);
        }
    }
}