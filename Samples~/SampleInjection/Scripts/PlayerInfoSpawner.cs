using System.Collections.Generic;
using AceLand.Injection;
using AceLand.Sample.Injection.Scripts.Models;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AceLand.Sample.Injection.Scripts
{
    public partial class PlayerInfoSpawner : UIBehaviour
    {
        [SerializeField] private Transform container;
        [SerializeField] private Button loadButton;
        [SerializeField] private Button clearButton;
        
        [Inject] private List<PlayerData> _players;
        [Inject] private IObjectPool<PlayerInfoUi> _pool;

        protected override void Awake()
        {
            if (container) return;
            
            gameObject.SetActive(false);
            
            Debug.LogWarning($"{nameof(PlayerInfoSpawner)} container is null!", this);
        }

        protected override void OnEnable()
        {
            loadButton?.onClick.AddListener(Load);
            clearButton?.onClick.AddListener(Clear);
        }

        protected override void OnDisable()
        {
            loadButton?.onClick.RemoveListener(Load);
            clearButton?.onClick.RemoveListener(Clear);
        }

        private void Load()
        {
            Clear();
            SpawnPlayerInfo();
        }

        private void Clear()
        {
            var items = container.GetComponentsInChildren<PlayerInfoUi>();
            foreach (var item in items)
                _pool.Return(item);
        }

        private void SpawnPlayerInfo()
        {
            if (_pool == null) return;
            
            foreach (var player in _players)
            {
                var ui = _pool.Rent();
                ui.transform.SetParent(container);
                ui.Initial(player);
            }
        }
    }
}