using UnityEngine;

namespace AceLand.Sample.Injection.Scripts.Services
{
    public sealed class Helper
    {
        private readonly string[] greetings = new[]
        {
            "Good Morning!",
            "Good Evening!",
            "Hi~~",
            "What's UP!",
            "Are you OK?"
        };
        
        private string RandomGreeting => 
            greetings[Random.Range(0, greetings.Length)];
        
        public string SayHi()
        {
            return RandomGreeting;
        }
    }
}