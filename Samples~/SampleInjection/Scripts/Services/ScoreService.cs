using UnityEngine;

namespace AceLand.Sample.Injection.Scripts.Services
{
    public interface IScoreService
    {
        int Score { get; }
        void Add(int amount);
        void Subtract(int amount);
    }
    
    internal sealed class ScoreService : IScoreService
    {
        private ScoreService()
        {
            Load();
        }

        private const string SCORE_KEY = "SCORE_TEST";
        
        public int Score { get; private set; }
        
        public void Add(int amount)
        {
            Score += amount;
            Verify();
            Save();
        }

        public void Subtract(int amount)
        {
            Score -= amount;
            Verify();
            Save();
        }
        
        private void Verify()
        {
            if (Score > 100) Score = 100;
            if (Score < 0) Score = 0;
        }

        private void Save()
        {
            PlayerPrefs.SetInt(SCORE_KEY, Score);
        }

        private void Load()
        {
            Score = PlayerPrefs.GetInt(SCORE_KEY, 0);
            Verify();
        }
    }
}