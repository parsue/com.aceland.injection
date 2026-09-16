using AceLand.Injection;
using AceLand.Sample.Injection.Scripts.Services;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AceLand.Sample.Injection.Scripts
{
    public partial class ScoreServiceInfo : UIBehaviour
    {
        [SerializeField] private TextMeshProUGUI scoreText;
        [SerializeField] private Button addButton;
        [SerializeField] private Button subtractButton;
        
        [Inject] private IScoreService _scoreService;

        protected override void Start()
        {
            SetScore();
        }

        protected override void OnEnable()
        {
            addButton.onClick.AddListener(AddScore);
            subtractButton.onClick.AddListener(SubtractScore);
        }

        protected override void OnDisable()
        {
            addButton.onClick.RemoveListener(AddScore);
            subtractButton.onClick.RemoveListener(SubtractScore);
        }

        private void SetScore()
        {
            scoreText?.SetText(_scoreService.Score.ToString());
        }

        private void AddScore()
        {
            _scoreService.Add(1);
            SetScore();
        }

        private void SubtractScore()
        {
            _scoreService.Subtract(1);
            SetScore();
        }
    }
}