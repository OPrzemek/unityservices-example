using System.Threading.Tasks;
using System;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Unity.Services.Relay.Models;
using Unity.Services.Relay;
using System.Net;

namespace Samples.HelloNetcode
{
    /// <summary>
    /// HUD implementation. Implements behaviour for the buttons, hosting server, joining client, and starting game.
    ///
    /// Text fields output the status of server and client registering with the relay server once the user presses
    /// the respective buttons.
    ///
    /// A bootstrap world is constructed to run the jobs for setting up host and client configuration for relay server.
    /// Once this is done the game can be launched and the configuration can be retrieved from the constructed world.
    /// </summary>
    public class RelayFrontend : MonoBehaviour
    {
        public InputField m_AddressInput;
        public Button ClientServerButton;
        public string m_Port;

        public string serverAddress = "127.0.0.1";
        public ushort serverPort = 7979;


        public Button JoinExistingGame;

        ConnectionState m_State;
        HostServer m_HostServerSystem;
        ConnectingPlayer m_HostClientSystem;

        enum ConnectionState
        {
            Unknown,
            SetupHost,
            SetupClient,
            JoinGame,
            JoinLocalGame,
        }

        public bool dedicatedServer;

        public void StartDedicatedServer()
        {
            var serverWorld = ClientServerBootstrap.CreateServerWorld("ServerWorld");

            var endpoint = NetworkEndpoint.AnyIpv4.WithPort(serverPort);
            {
                using var networkDriverQuery = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
                networkDriverQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.Listen(NetworkEndpoint.AnyIpv4);
            }
        }

        public void OnConnectToServer()
        {
            var client = ClientServerBootstrap.CreateClientWorld("ClientWorld");

            DestroyLocalSimulationWorld();
            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = client;
            SceneManager.LoadSceneAsync("SampleScene");

            var endpoint = NetworkEndpoint.Parse(serverAddress, serverPort);
            {
                using var drvQuery = client.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
                drvQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(client.EntityManager, endpoint);
            }
        }

        private void Start()
        {
            if (dedicatedServer)
            {
                StartDedicatedServer();
                return;
            }
            Debug.Log("Relay enabled!");
            m_AddressInput.text = string.Empty;
            m_AddressInput.placeholder.GetComponent<Text>().text = "Join Code for Host Server";
        }
        public async Task<string> OnSetupHostAsync()
        {
            m_State = ConnectionState.SetupHost;
            HostServer();

            // Wait for the join code with a timeout
            const int maxWaitTimeMs = 10000; // 10 seconds timeout
            const int checkIntervalMs = 100; // Check every 100ms

            var joinCode = await Task.Run(async () =>
            {
                var elapsedTime = 0;
                while (string.IsNullOrEmpty(m_HostServerSystem.JoinCode) && elapsedTime < maxWaitTimeMs)
                {
                    await Task.Delay(checkIntervalMs);
                    elapsedTime += checkIntervalMs;
                }
                return m_HostServerSystem.JoinCode;
            });

            if (string.IsNullOrEmpty(joinCode))
            {
                throw new TimeoutException("Timed out waiting for join code");
            }

            return joinCode;
        }

        public void OnSetupHost()
        {
            Debug.Log("OnSetupHost");
            m_State = ConnectionState.SetupHost;
        }

        public void OnSetupClient()
        {
            Debug.Log("OnSetupClient");
            m_State = ConnectionState.SetupClient;
        }

        public void Update()
        {
            switch (m_State)
            {
                case ConnectionState.SetupHost:
                    {
                        HostServer();
                        m_State = ConnectionState.SetupClient;
                        goto case ConnectionState.SetupClient;
                    }
                case ConnectionState.SetupClient:
                    {
                        var isServerHostedLocally = m_HostServerSystem?.RelayServerData.Endpoint.IsValid;
                        var enteredJoinCode = !string.IsNullOrEmpty(m_AddressInput.text);
                        if (isServerHostedLocally.GetValueOrDefault())
                        {
                            SetupClient();
                            m_HostClientSystem.GetJoinCodeFromHost();
                            m_State = ConnectionState.JoinLocalGame;
                            goto case ConnectionState.JoinLocalGame;
                        }

                        if (enteredJoinCode)
                        {
                            JoinAsClient();
                            m_State = ConnectionState.JoinGame;
                            goto case ConnectionState.JoinGame;
                        }
                        break;
                    }
                case ConnectionState.JoinGame:
                    {
                        var hasClientConnectedToRelayService = m_HostClientSystem?.RelayClientData.Endpoint.IsValid;
                        if (hasClientConnectedToRelayService.GetValueOrDefault())
                        {
                            ConnectToRelayServer();
                            m_State = ConnectionState.Unknown;
                        }
                        break;
                    }
                case ConnectionState.JoinLocalGame:
                    {
                        var hasClientConnectedToRelayService = m_HostClientSystem?.RelayClientData.Endpoint.IsValid;
                        if (hasClientConnectedToRelayService.GetValueOrDefault())
                        {
                            SetupRelayHostedServerAndConnect();
                            m_State = ConnectionState.Unknown;
                        }
                        break;
                    }
                case ConnectionState.Unknown:
                default: return;
            }
        }

        void HostServer()
        {
            var world = World.All[0];
            m_HostServerSystem = world.GetOrCreateSystemManaged<HostServer>();
            var enableRelayServerEntity = world.EntityManager.CreateEntity(ComponentType.ReadWrite<EnableRelayServer>());
            world.EntityManager.AddComponent<EnableRelayServer>(enableRelayServerEntity);

            var simGroup = world.GetExistingSystemManaged<SimulationSystemGroup>();
            simGroup.AddSystemToUpdateList(m_HostServerSystem);
        }

        void SetupClient()
        {
            var world = World.All[0];
            m_HostClientSystem = world.GetOrCreateSystemManaged<ConnectingPlayer>();
            var simGroup = world.GetExistingSystemManaged<SimulationSystemGroup>();
            simGroup.AddSystemToUpdateList(m_HostClientSystem);
        }

        void JoinAsClient()
        {
            SetupClient();
            var world = World.All[0];
            var enableRelayServerEntity = world.EntityManager.CreateEntity(ComponentType.ReadWrite<EnableRelayServer>());
            world.EntityManager.AddComponent<EnableRelayServer>(enableRelayServerEntity);
            m_HostClientSystem.JoinUsingCode(m_AddressInput.text);
        }

        /// <summary>
        /// Collect relay server end point from completed systems. Set up server with relay support and connect client
        /// to hosted server through relay server.
        /// Both client and server world is manually created to allow us to override the <see cref="DriverConstructor"/>.
        ///
        /// Two singleton entities are constructed with listen and connect requests. These will be executed asynchronously.
        /// Connecting to relay server will not be bound immediately. The Request structs will ensure that we
        /// continuously poll until the connection is established.
        /// </summary>
        string SetupRelayHostedServerAndConnect()
        {
            if (ClientServerBootstrap.RequestedPlayType != ClientServerBootstrap.PlayType.ClientAndServer)
            {
                Debug.LogError($"Creating client/server worlds is not allowed if playmode is set to {ClientServerBootstrap.RequestedPlayType}");
                return $"Creating client/server worlds is not allowed if playmode is set to {ClientServerBootstrap.RequestedPlayType}";
            }

            var world = World.All[0];
            var relayClientData = world.GetExistingSystemManaged<ConnectingPlayer>().RelayClientData;
            var relayServerData = world.GetExistingSystemManaged<HostServer>().RelayServerData;
            var joinCode = world.GetExistingSystemManaged<HostServer>().JoinCode;

            Debug.Log(joinCode);

            var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
            NetworkStreamReceiveSystem.DriverConstructor = new RelayDriverConstructor(relayServerData, relayClientData);
            var server = ClientServerBootstrap.CreateServerWorld("ServerWorld");
            var client = ClientServerBootstrap.CreateClientWorld("ClientWorld");
            NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;

            SceneManager.LoadSceneAsync("SampleScene");

            //Destroy the local simulation world to avoid the game scene to be loaded into it
            //This prevent rendering (rendering from multiple world with presentation is not greatly supported)
            //and other issues.
            DestroyLocalSimulationWorld();
            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = server;

            var joinCodeEntity = server.EntityManager.CreateEntity(ComponentType.ReadOnly<JoinCode>());
            server.EntityManager.SetComponentData(joinCodeEntity, new JoinCode { Value = joinCode });

            var networkStreamEntity = server.EntityManager.CreateEntity(ComponentType.ReadWrite<NetworkStreamRequestListen>());
            server.EntityManager.SetName(networkStreamEntity, "NetworkStreamRequestListen");
            server.EntityManager.SetComponentData(networkStreamEntity, new NetworkStreamRequestListen { Endpoint = NetworkEndpoint.AnyIpv4 });

            networkStreamEntity = client.EntityManager.CreateEntity(ComponentType.ReadWrite<NetworkStreamRequestConnect>());
            client.EntityManager.SetName(networkStreamEntity, "NetworkStreamRequestConnect");
            // For IPC this will not work and give an error in the transport layer. For this sample we force the client to connect through the relay service.
            // For a locally hosted server, the client would need to connect to NetworkEndpoint.AnyIpv4, and the relayClientData.Endpoint in all other cases.
            client.EntityManager.SetComponentData(networkStreamEntity, new NetworkStreamRequestConnect { Endpoint = relayClientData.Endpoint });
            m_AddressInput.text = joinCode;
            return joinCode;
        }

        public void GetJoinCode()
        {
            // Znajd� �wiat serwera
            World serverWorld = null;
            foreach (var world in World.All)
            {
                if (world.Name == "ServerWorld")
                {
                    serverWorld = world;
                    break;
                }
            }

            if (serverWorld == null)
            {
                Debug.LogError("Server world not found!");
            }

            // Utw�rz zapytanie o encj� z komponentem JoinCode
            EntityQuery joinCodeQuery = serverWorld.EntityManager.CreateEntityQuery(typeof(JoinCode));

            if (joinCodeQuery.IsEmpty)
            {
                Debug.LogError("JoinCode entity not found!");
            }

            // Pobierz komponent JoinCode
            JoinCode joinCode = joinCodeQuery.GetSingleton<JoinCode>();

            // Zwr�� warto�� kodu do��czenia
            Debug.Log(joinCode.Value.ToString());
        }

        void ConnectToRelayServer()
        {
            var world = World.All[0];
            var relayClientData = world.GetExistingSystemManaged<ConnectingPlayer>().RelayClientData;

            var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
            NetworkStreamReceiveSystem.DriverConstructor = new RelayDriverConstructor(new RelayServerData(), relayClientData);
            var client = ClientServerBootstrap.CreateClientWorld("ClientWorld");
            NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;

            SceneManager.LoadScene("SampleScene");

            //Destroy the local simulation world to avoid the game scene to be loaded into it
            //This prevent rendering (rendering from multiple world with presentation is not greatly supported)
            //and other issues.
            DestroyLocalSimulationWorld();
            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = client;

            var networkStreamEntity = client.EntityManager.CreateEntity(ComponentType.ReadWrite<NetworkStreamRequestConnect>());
            client.EntityManager.SetName(networkStreamEntity, "NetworkStreamRequestConnect");
            // For IPC this will not work and give an error in the transport layer. For this sample we force the client to connect through the relay service.
            // For a locally hosted server, the client would need to connect to NetworkEndpoint.AnyIpv4, and the relayClientData.Endpoint in all other cases.
            client.EntityManager.SetComponentData(networkStreamEntity, new NetworkStreamRequestConnect { Endpoint = relayClientData.Endpoint });
        }

        protected void DestroyLocalSimulationWorld()
        {
            foreach (var world in World.All)
            {
                if (world.Flags == WorldFlags.Game)
                {
                    world.Dispose();
                    break;
                }
            }
        }

    }
}