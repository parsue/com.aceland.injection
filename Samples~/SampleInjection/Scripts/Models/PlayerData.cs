using UnityEngine;

namespace AceLand.Sample.Injection.Scripts.Models
{
    public enum PlayerClass
    {
        Hero, Warrior, Mage, Rogue
    }
    
    [System.Serializable]
    public class PlayerData
    {
        [SerializeField] private string playerName;
        [SerializeField] private PlayerClass playerClass;
        [SerializeField, Range(1, 20)] private int level = 1;
        [SerializeField, Range(1, 20)] private int maxLevel = 20;
        [SerializeField, Range(8, 36)] private int hp = 8;
        [SerializeField, Range(8, 36)] private int maxHp = 36;
        
        public string PlayerName => playerName;
        public PlayerClass PlayerClass => playerClass;
        public int Level => level;
        public int MaxLevel => maxLevel;
        public int Hp => hp;
        public int MaxHp => maxHp;

        public void Validate()
        {
            if (level > maxLevel)
                level = maxLevel;
            
            if (hp > maxHp)
                hp = maxHp;
        }
    }
}