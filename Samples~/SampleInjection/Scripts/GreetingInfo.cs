using AceLand.Injection;
using AceLand.Sample.Injection.Scripts.Services;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AceLand.Sample.Injection.Scripts
{
    public partial class GreetingInfo : UIBehaviour
    {
        [SerializeField] private TextMeshProUGUI text;
        [SerializeField] private Button nextButton;
        
        [Inject] private Helper _helper;

        protected override void Start()
        {
            SetGreeting();
        }

        protected override void OnEnable()
        {
            nextButton.onClick.AddListener(SetGreeting);
        }

        protected override void OnDisable()
        {
            nextButton.onClick.RemoveListener(SetGreeting);
        }
        private void SetGreeting()
        {
            text?.SetText(_helper.SayHi());
        }
    }
}