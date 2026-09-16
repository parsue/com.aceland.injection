using AceLand.Sample.Injection.Scripts.Models;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AceLand.Sample.Injection.Scripts
{
    public class PlayerInfoUi : UIBehaviour
    {
        [SerializeField] private TextMeshProUGUI nameText;
        [SerializeField] private TextMeshProUGUI classText;
        [SerializeField] private TextMeshProUGUI levelText;
        [SerializeField] private TextMeshProUGUI hpText;
        
        public void Initial(PlayerData playerData)
        {
            nameText?.SetText(playerData.PlayerName);
            classText?.SetText(playerData.PlayerClass.ToString());
            levelText?.SetText($"{playerData.Level} / {playerData.MaxLevel}");
            hpText?.SetText($"{playerData.Hp} / {playerData.MaxHp}");
        }
    }
}