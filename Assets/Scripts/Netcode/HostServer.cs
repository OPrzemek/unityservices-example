
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Entities;
using UnityEngine;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using System.Runtime.InteropServices;
using UnityEngine.UIElements;

/// <summary>
/// Responsible for contacting relay server and setting up <see cref="RelayServerData"/> and <see cref="JoinCode"/>.
/// Steps include:
/// 1. Initializing services
/// 2. Logging in
/// 3. Allocating number of players that are allowed to join.
/// 4. Retrieving join code
/// 5. Getting relay server information. I.e. IP-address, etc.
/// </summary>
[DisableAutoCreation]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class HostServer : SystemBase
{
    const int RelayMaxConnections = 2;
    public string JoinCode;

    public RelayServerData RelayServerData;
    public Lobby HostedLobby;
    HostStatus m_HostStatus;
    Task<List<Region>> m_RegionsTask;
    Task<Allocation> m_AllocationTask;
    Task<string> m_JoinCodeTask;
    Task m_InitializeTask;
    Task m_SignInTask;
    Task<Lobby> m_CreateLobbyTask;

    [Flags]
    enum HostStatus
    {
        Unknown,
        InitializeServices,
        Initializing,
        SigningIn,
        FailedToHost,
        Ready,
        GettingRegions,
        Allocating,
        GettingJoinCode,
        GetRelayData,
        CreateLobby,
        CreatingLobby
    }

    protected override void OnCreate()
    {
        RequireForUpdate<EnableRelayServer>();
        m_HostStatus = HostStatus.InitializeServices;
    }

    protected override void OnUpdate()
    {
        switch (m_HostStatus)
        {
            case HostStatus.FailedToHost:
                {
                    m_HostStatus = HostStatus.Unknown;
                    return;
                }
            case HostStatus.Ready:
                {
                    m_HostStatus = HostStatus.Unknown;
                    return;
                }
            case HostStatus.InitializeServices:
                {
                    m_InitializeTask = UnityServices.InitializeAsync();
                    m_HostStatus = HostStatus.Initializing;
                    return;
                }
            case HostStatus.Initializing:
                {
                    m_HostStatus = WaitForInitialization(m_InitializeTask, out m_SignInTask);
                    return;
                }
            case HostStatus.SigningIn:
                {
                    m_HostStatus = WaitForSignIn(m_SignInTask, out m_RegionsTask);
                    return;
                }
            case HostStatus.GettingRegions:
                {
                    m_HostStatus = WaitForRegions(m_RegionsTask, out m_AllocationTask);
                    return;
                }
            case HostStatus.Allocating:
                {
                    m_HostStatus = WaitForAllocations(m_AllocationTask, out m_JoinCodeTask);
                    return;
                }
            case HostStatus.GettingJoinCode:
                {
                    m_HostStatus = WaitForJoin(m_JoinCodeTask, out JoinCode);
                    return;
                }
            case HostStatus.GetRelayData:
                {
                    m_HostStatus = BindToHost(m_AllocationTask, out RelayServerData);
                    return;
                }
            case HostStatus.CreateLobby:
                {
                    m_CreateLobbyTask = LobbyService.Instance.CreateLobbyAsync("Test lobby", 2);
                    m_HostStatus = HostStatus.CreatingLobby;
                    return;
                }
            case HostStatus.CreatingLobby:
                {
                    m_HostStatus = WaitForCreateLobby(m_CreateLobbyTask, RelayServerData, JoinCode, out HostedLobby);
                    return;
                }
            case HostStatus.Unknown:
            default:
                break;
        }
    }

    static HostStatus WaitForCreateLobby(Task<Lobby> createLobbyTask, RelayServerData relayData, string joinCode, out Lobby hostedLobby)
    {
        if (!createLobbyTask.IsCompleted)
        {
            hostedLobby = default;
            return HostStatus.CreatingLobby;
        }

        if (createLobbyTask.IsFaulted)
        {
            Debug.LogError("Failed to create lobby");
            Debug.LogException(createLobbyTask.Exception);
            hostedLobby = default;
            return HostStatus.FailedToHost;
        }

        hostedLobby = createLobbyTask.Result;

        //var playerOptions = new UpdatePlayerOptions
        //{
        //    AllocationId = relayData.AllocationId.AsString()
        //};
        //LobbyService.Instance.UpdatePlayerAsync(hostedLobby.Id, hostedLobby.HostId, playerOptions);

        Debug.Log("Lobby join code: " + hostedLobby.LobbyCode);

        Lobbies.Instance.UpdateLobbyAsync(hostedLobby.Id, new UpdateLobbyOptions
        {
            IsLocked = false,
            IsPrivate = false,

            Data = new Dictionary<string, DataObject>
            {
                { "RELAY_CODE", new DataObject(DataObject.VisibilityOptions.Member, joinCode) }
            }
        });


        return HostStatus.Ready;
    }

    static HostStatus WaitForSignIn(Task signInTask, out Task<List<Region>> regionTask)
    {
        if (!signInTask.IsCompleted)
        {
            regionTask = default;
            return HostStatus.SigningIn;
        }

        if (signInTask.IsFaulted)
        {
            Debug.LogError("Signing in failed");
            Debug.LogException(signInTask.Exception);
            regionTask = default;
            return HostStatus.FailedToHost;
        }

        // Request list of valid regions
        regionTask = RelayService.Instance.ListRegionsAsync();
        return HostStatus.GettingRegions;
    }

    static HostStatus WaitForInitialization(Task initializeTask, out Task nextTask)
    {
        if (!initializeTask.IsCompleted)
        {
            nextTask = default;
            return HostStatus.Initializing;
        }

        if (initializeTask.IsFaulted)
        {
            Debug.LogError("UnityServices Initialization failed");
            Debug.LogException(initializeTask.Exception);
            nextTask = default;
            return HostStatus.FailedToHost;
        }

        if (AuthenticationService.Instance.IsSignedIn)
        {
            nextTask = Task.CompletedTask;
            return HostStatus.SigningIn;
        }
        else
        {
            nextTask = AuthenticationService.Instance.SignInAnonymouslyAsync();
            return HostStatus.SigningIn;
        }
    }

    // Bind and listen to the Relay server
    static HostStatus BindToHost(Task<Allocation> allocationTask, out RelayServerData relayServerData)
    {
        var allocation = allocationTask.Result;
        try
        {
            // Format the server data, based on desired connectionType
            relayServerData = HostRelayData(allocation);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            relayServerData = default;
            return HostStatus.FailedToHost;
        }
        return HostStatus.CreateLobby;
    }

    // Get the Join Code, you can then share it with the clients so they can join
    static HostStatus WaitForJoin(Task<string> joinCodeTask, out string joinCode)
    {
        joinCode = null;
        if (!joinCodeTask.IsCompleted)
        {
            return HostStatus.GettingJoinCode;
        }

        if (joinCodeTask.IsFaulted)
        {
            Debug.LogError("Create join code request failed");
            Debug.LogException(joinCodeTask.Exception);
            return HostStatus.FailedToHost;
        }

        joinCode = joinCodeTask.Result;
        return HostStatus.GetRelayData;
    }

    static HostStatus WaitForAllocations(Task<Allocation> allocationTask, out Task<string> joinCodeTask)
    {
        if (!allocationTask.IsCompleted)
        {
            joinCodeTask = null;
            return HostStatus.Allocating;
        }

        if (allocationTask.IsFaulted)
        {
            Debug.LogError("Create allocation request failed");
            Debug.LogException(allocationTask.Exception);
            joinCodeTask = null;
            return HostStatus.FailedToHost;
        }

        // Request the join code to the Relay service
        var allocation = allocationTask.Result;
        joinCodeTask = RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
        return HostStatus.GettingJoinCode;
    }

    static HostStatus WaitForRegions(Task<List<Region>> collectRegionTask, out Task<Allocation> allocationTask)
    {
        if (!collectRegionTask.IsCompleted)
        {
            allocationTask = null;
            return HostStatus.GettingRegions;
        }

        if (collectRegionTask.IsFaulted)
        {
            Debug.LogError("List regions request failed");
            Debug.LogException(collectRegionTask.Exception);
            allocationTask = null;
            return HostStatus.FailedToHost;
        }

        var regionList = collectRegionTask.Result;
        // pick a region from the list
        var targetRegion = regionList[0].Id;

        // Request an allocation to the Relay service
        // with a maximum of 5 peer connections, for a maximum of 6 players.
        allocationTask = RelayService.Instance.CreateAllocationAsync(RelayMaxConnections, targetRegion);
        return HostStatus.Allocating;
    }

    // connectionType also supports udp, but this is not recommended
    static RelayServerData HostRelayData(Allocation allocation, string connectionType = "dtls")
    {
        // Select endpoint based on desired connectionType
        var endpoint = RelayUtilities.GetEndpointForConnectionType(allocation.ServerEndpoints, connectionType);
        if (endpoint == null)
        {
            throw new InvalidOperationException($"endpoint for connectionType {connectionType} not found");
        }

        // Prepare the server endpoint using the Relay server IP and port
        var serverEndpoint = NetworkEndpoint.Parse(endpoint.Host, (ushort)endpoint.Port);

        // UTP uses pointers instead of managed arrays for performance reasons, so we use these helper functions to convert them
        var allocationIdBytes = RelayAllocationId.FromByteArray(allocation.AllocationIdBytes);
        var connectionData = RelayConnectionData.FromByteArray(allocation.ConnectionData);
        var key = RelayHMACKey.FromByteArray(allocation.Key);

        // Prepare the Relay server data and compute the nonce value
        // The host passes its connectionData twice into this function
        var relayServerData = new RelayServerData(ref serverEndpoint, 0, ref allocationIdBytes, ref connectionData,
            ref connectionData, ref key, connectionType == "dtls");

        return relayServerData;
    }
}
