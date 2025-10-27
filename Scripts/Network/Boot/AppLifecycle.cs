using UnityEngine;
using FishNet;
using FishNet.Managing;

namespace NetBoot
{
    /// <summary>
    /// Gestisce lo shutdown ordinato di FishNet ed espone un flag globale quando l'app chiude.
    /// Metti questo su un GameObject nella scena bootstrap (insieme al NetworkManager) e marca la scena/bootstrap come persistente.
    /// </summary>
    public class AppLifecycle : MonoBehaviour
    {
        public static bool Quitting { get; private set; }

        [Tooltip("Se true, mette DontDestroyOnLoad su questo GameObject (consigliato nella scena bootstrap).")]
        public bool dontDestroyOnLoad = true;

        void Awake()
        {
            if (dontDestroyOnLoad)
                DontDestroyOnLoad(gameObject);

            Application.quitting += OnAppQuitting;
        }

        void OnAppQuitting()
        {
            Quitting = true;

            // Spegnimento ordinato FishNet (se ancora presente)
            var nm = InstanceFinder.NetworkManager;
            if (nm != null)
            {
                try
                {
                    if (nm.ServerManager != null && nm.ServerManager.Started)
                        nm.ServerManager.StopConnection(true);

                    if (nm.ClientManager != null && nm.ClientManager.Started)
                        nm.ClientManager.StopConnection();
                }
                catch { /* ignora eccezioni di teardown */ }
            }
        }
    }
}
